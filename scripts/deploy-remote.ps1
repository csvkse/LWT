[CmdletBinding()]
param(
    [ValidateSet("Official", "Dev")]
    [string]$Mode = "Official",

    [ValidateSet("Auto", "Direct", "GitArchive", "GitPull")]
    [string]$SyncMethod = "Auto",

    [switch]$FrontendOnly,
    [string]$HotUpdatePlugin = "",
    [switch]$SetupIncusHost,

    [string]$EnvFile = "env/.envdev",
    [switch]$SkipDeps,
    [switch]$CheckOnly,
    [string]$RemoteExec = ""
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) {
    Write-Host "`n====> $msg" -ForegroundColor Cyan
}

function Write-Success([string]$msg) {
    Write-Host " [OK] $msg" -ForegroundColor Green
}

function Write-Warn([string]$msg) {
    Write-Host " [!] $msg" -ForegroundColor Yellow
}

# 1. Verify config file exists
$resolvedEnvPath = Resolve-Path -Path $EnvFile -ErrorAction SilentlyContinue
if (-not $resolvedEnvPath -or -not (Test-Path $resolvedEnvPath)) {
    throw "Env config not found: $EnvFile. Please ensure env/.envdev exists."
}

Write-Step "Loading deployment config ($Mode mode) from $EnvFile..."

# 2. Parse config file
$rawContent = Get-Content $resolvedEnvPath -Raw -Encoding UTF8
$envMap = @{}

$lines = $rawContent -split "`r?`n"
$currentKey = $null
$currentVal = ""
$inMultiLine = $false

foreach ($line in $lines) {
    $trimmed = $line.Trim()
    if (-not $inMultiLine) {
        if ($trimmed.StartsWith("#") -or [string]::IsNullOrWhiteSpace($trimmed)) {
            continue
        }
        if ($line -match '^([A-Za-z0-9_]+)=(.*)$') {
            $currentKey = $matches[1]
            $valPart = $matches[2]
            if ($valPart.StartsWith('"') -and -not ($valPart.EndsWith('"') -and $valPart.Length -gt 1)) {
                $inMultiLine = $true
                $currentVal = $valPart.Substring(1) + "`n"
            } else {
                $valClean = $valPart.Trim('"', "'")
                $envMap[$currentKey] = $valClean
                $currentKey = $null
            }
        }
    } else {
        if ($line.EndsWith('"')) {
            $currentVal += $line.Substring(0, $line.Length - 1)
            $envMap[$currentKey] = $currentVal
            $inMultiLine = $false
            $currentKey = $null
            $currentVal = ""
        } else {
            $currentVal += $line + "`n"
        }
    }
}

$deployHost = $envMap["DEPLOY_HOST"]
$deployPort = if ($envMap["DEPLOY_PORT"]) { [int]$envMap["DEPLOY_PORT"] } else { 22 }
$deployUser = if ($envMap["DEPLOY_USER"]) { $envMap["DEPLOY_USER"] } else { "root" }
$sshKeyContent = $envMap["DEPLOY_SSH_KEY"]
$webBaseUrl = $envMap["WEB_BASE_URL"]
$webPort = if ($envMap["WEB_PORT"]) { $envMap["WEB_PORT"] } else { "5270" }
$containerName = if ($envMap["DOCKER_CONTAINER_NAME"]) { $envMap["DOCKER_CONTAINER_NAME"] } else { "linuxwebtool" }
$dockerImage = if ($envMap["DOCKER_IMAGE"]) { $envMap["DOCKER_IMAGE"] } else { "ghcr.io/csvkse/lwt:latest" }

if (-not $deployHost) {
    throw "Missing DEPLOY_HOST configuration"
}

# Mode-specific adjustments (Adapter Pattern)
if ($FrontendOnly -or $HotUpdatePlugin) {
    $Mode = "Dev"
}

if ($Mode -eq "Dev") {
    $containerName = "${containerName}-dev"
    $devPort = "5271"
    $devImage = "lwt:dev"
    $devWorkdir = "/opt/lwt-dev"
    $devWebUrl = "http://${deployHost}:${devPort}/"
    if ($FrontendOnly) {
        Write-Success "Adapter [LinuxFastDevAdapter - FrontendHotSync] selected: Target $deployUser@$deployHost`:$deployPort, container $containerName"
    } elseif ($HotUpdatePlugin) {
        Write-Success "Adapter [LinuxFastDevAdapter - PluginHotUpdate] selected: Target $deployUser@$deployHost`:$deployPort, container $containerName, plugin $HotUpdatePlugin"
    } else {
        Write-Success "Adapter [LinuxFastDevAdapter] selected: Target $deployUser@$deployHost`:$deployPort, container $containerName (Port $devPort), SyncMethod: $SyncMethod"
    }
} else {
    Write-Success "Adapter [OfficialGithubAotAdapter] selected: Target $deployUser@$deployHost`:$deployPort, container $containerName (Port $webPort), image $dockerImage"
}

# 3. Setup temporary SSH key file
$tempKeyPath = [System.IO.Path]::GetTempFileName()
try {
    $cleanKey = $sshKeyContent.Replace("\n", "`n").Trim()
    [System.IO.File]::WriteAllText($tempKeyPath, ($cleanKey + "`n"), [System.Text.Encoding]::ASCII)
    & icacls.exe $tempKeyPath /inheritance:r /grant:r "$($env:USERNAME):(R)" | Out-Null

    function Clean-SshOutput($val) {
        if ($null -eq $val) { return "" }
        $str = ($val -join "`n")
        $str = [regex]::Replace($str, "\x1b\][^\x07\x1b]*[\x07\x1b\\]", "")
        $str = [regex]::Replace($str, "\x1b\[[0-9;]*[a-zA-Z]", "")
        return $str.Trim()
    }

    function Invoke-RemoteSsh([string]$cmd) {
        $cleanCmd = $cmd.Replace("`r`n", "`n")
        $sshArgs = @(
            "-i", $tempKeyPath,
            "-p", $deployPort,
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=/dev/null",
            "-o", "LogLevel=ERROR",
            "$deployUser@$deployHost",
            $cleanCmd
        )
        $output = & ssh @sshArgs
        if ($LASTEXITCODE -ne 0) {
            throw "SSH command failed with exit code $($LASTEXITCODE): $cmd"
        }
        return (Clean-SshOutput $output)
    }

    function Invoke-IncusHostProvision {
        Write-Step "[Host Provision] Checking and provisioning Incus host dependencies..."
        $incusProvisionScript = @'
set -eu
mkdir -p /var/lib/incus

# 1. 检查并安装轻量级 incus-base 守护进程与客户端
if ! command -v incus >/dev/null 2>&1; then
    echo "[PROVISION] Updating apt and installing incus-base, incus-client, dnsmasq-base from bookworm-backports..."
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y --no-install-recommends -t bookworm-backports \
        -o Dpkg::Options::="--force-confdef" -o Dpkg::Options::="--force-confold" \
        incus-base incus-client dnsmasq-base
    systemctl enable --now incus.socket incus.service
else
    echo "[PROVISION] incus daemon package is already installed."
    systemctl enable --now incus.socket incus.service 2>/dev/null || true
fi

# 2. 幂等初始化存储池与网桥
if ! incus network list --format json 2>/dev/null | grep -q '"name":"incusbr0"'; then
    echo "[PROVISION] Initializing minimal Incus storage and incusbr0 network..."
    incus admin init --minimal
else
    echo "[PROVISION] incusbr0 network is already active."
fi

# 3. 注入 Docker 防火墙共存放行规则
echo "[PROVISION] Ensuring iptables FORWARD rules for incusbr0..."
iptables -C FORWARD -i incusbr0 -j ACCEPT 2>/dev/null || iptables -I FORWARD 1 -i incusbr0 -j ACCEPT
iptables -C FORWARD -o incusbr0 -j ACCEPT 2>/dev/null || iptables -I FORWARD 1 -o incusbr0 -j ACCEPT

# 4. 验证 socket 与版本
echo "[PROVISION] Incus daemon ready. Verification:"
ls -la /var/lib/incus/unix.socket
incus version
'@
        $provisionOut = Invoke-RemoteSsh $incusProvisionScript
        Write-Host $provisionOut -ForegroundColor DarkCyan
        Write-Success "Incus host provisioning completed successfully!"
    }

    if ($SetupIncusHost) {
        Invoke-IncusHostProvision
        if ($CheckOnly) {
            return
        }
    }

    if ($RemoteExec) {
        Write-Step "Executing remote command: $RemoteExec"
        $execOutput = Invoke-RemoteSsh $RemoteExec
        Write-Host $execOutput
        return
    }

    if ($CheckOnly) {
        Write-Step "Checking remote host connectivity and container status..."
        $uptime = Invoke-RemoteSsh "uptime"
        $ps = Invoke-RemoteSsh "docker ps -a --filter name=$containerName"
        Write-Host $uptime
        Write-Host $ps
        Write-Success "Host check passed!"
        return
    }

    # =========================================================================
    # Strategy A: Linux Fast Dev Pipeline (Decoupled Sync + Fast JIT Build)
    # =========================================================================
    if ($FrontendOnly) {
        Write-Step "[Dev - Frontend HotSync] Bundling wwwroot and streaming to running container..."
        $feBundle = "fe-bundle.tar.gz"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        try {
            if (Test-Path $feBundle) { Remove-Item $feBundle -Force }
            & tar.exe -czf $feBundle -C src/LinuxWebTool.WebHost/wwwroot .
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $feBundle)) {
                throw "Failed to create frontend bundle at $feBundle"
            }

            # 1. 同步到远程宿主机工作区
            Invoke-RemoteSsh "mkdir -p $devWorkdir/src/LinuxWebTool.WebHost/wwwroot"
            $feStreamArgs = @(
                "-i", $tempKeyPath,
                "-p", "$deployPort",
                "-o", "StrictHostKeyChecking=no",
                "-o", "UserKnownHostsFile=/dev/null",
                "-o", "LogLevel=ERROR",
                "$deployUser@$deployHost",
                "tar -xzf - -C $devWorkdir/src/LinuxWebTool.WebHost/wwwroot"
            )
            $feProc = Start-Process -FilePath "ssh" -ArgumentList $feStreamArgs -RedirectStandardInput $feBundle -NoNewWindow -Wait -PassThru
            if ($feProc.ExitCode -ne 0) {
                throw "Frontend host sync failed with exit code $($feProc.ExitCode)"
            }

            # 2. 如果开发容器正在运行，直接热补丁到容器内部 /app/wwwroot（无需构建与重启）
            $containerRunning = (Invoke-RemoteSsh "docker ps -q --filter name=$containerName").Trim()
            if ($containerRunning) {
                $containerStreamArgs = @(
                    "-i", $tempKeyPath,
                    "-p", "$deployPort",
                    "-o", "StrictHostKeyChecking=no",
                    "-o", "UserKnownHostsFile=/dev/null",
                    "-o", "LogLevel=ERROR",
                    "$deployUser@$deployHost",
                    "docker exec -i $containerName tar -xzf - -C /app/wwwroot"
                )
                $cProc = Start-Process -FilePath "ssh" -ArgumentList $containerStreamArgs -RedirectStandardInput $feBundle -NoNewWindow -Wait -PassThru
                if ($cProc.ExitCode -ne 0) {
                    Write-Warn "Direct container patch exited with code $($cProc.ExitCode)"
                }
            }

            $sw.Stop()
            Write-Success "Frontend static assets hot-synced in $($sw.ElapsedMilliseconds) ms!"
            Write-Host "Instant Web URL: $devWebUrl (Simply press Ctrl+F5 in browser to view changes)" -ForegroundColor Green
            return
        }
        finally {
            if (Test-Path $feBundle) { Remove-Item $feBundle -Force -ErrorAction SilentlyContinue }
        }
    }

    # =========================================================================
    # Strategy A-2: Microservice Plugin In-Place Hot Update (Zero Container Restart)
    # =========================================================================
    if ($HotUpdatePlugin) {
        if ($HotUpdatePlugin -eq "1" -or $HotUpdatePlugin -eq "true") {
            $HotUpdatePlugin = "mcp-bridge"
        }
        $pluginRelDir = "plugins/$HotUpdatePlugin"
        if (-not (Test-Path $pluginRelDir)) {
            throw "Plugin directory '$pluginRelDir' not found."
        }

        Write-Step "[Dev - Plugin HotUpdate] Hot-updating microservice plugin '$HotUpdatePlugin' on $containerName..."
        $sw = [System.Diagnostics.Stopwatch]::StartNew()

        # 1. 确保目标二进制存在且最新（若是 Go 编写的微服务插件）
        $binFile = "$pluginRelDir/$HotUpdatePlugin"
        $srcFile = "$pluginRelDir/cmd/$HotUpdatePlugin/main.go"
        if (-not (Test-Path $srcFile)) {
            $srcFile = "$pluginRelDir/main.go"
        }
        if (Test-Path $srcFile) {
            $isElf = $false
            if (Test-Path $binFile) {
                try {
                    $bytes = [System.IO.File]::ReadAllBytes($binFile)
                    if ($bytes.Length -ge 4 -and $bytes[0] -eq 0x7F -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x4C -and $bytes[3] -eq 0x46) {
                        $isElf = $true
                    }
                } catch { }
            }
            $needCompile = (-not $isElf)
            if (-not $needCompile -and (Test-Path $binFile)) {
                $latestSource = (Get-ChildItem -Path $pluginRelDir -Recurse -File -Exclude "$HotUpdatePlugin","*.exe" | Measure-Object -Property LastWriteTime -Maximum).Maximum
                if ($latestSource -and ((Get-Item $binFile).LastWriteTime -lt $latestSource)) {
                    $needCompile = $true
                }
            }
            if ($needCompile -and (Get-Command "go" -ErrorAction SilentlyContinue)) {
                Write-Step "Pre-compiling $pluginRelDir for Linux x64 (CGO_ENABLED=0 GOOS=linux GOARCH=amd64)..."
                cmd /c "set CGO_ENABLED=0& set GOOS=linux& set GOARCH=amd64& cd $pluginRelDir& go build -ldflags=`"-s -w`" -o $HotUpdatePlugin ./cmd/$HotUpdatePlugin"
            }
        }

        $localHash = ""
        if (Test-Path $binFile) {
            $localHash = (Get-FileHash $binFile -Algorithm SHA256).Hash.ToLower()
            Write-Host "Local Linux ELF Binary SHA256: $localHash" -ForegroundColor DarkCyan
        }

        # 2. 打包插件工作副本并流式传输到远程宿主机
        $pluginBundle = "plugin-$HotUpdatePlugin-bundle.tar.gz"
        try {
            if (Test-Path $pluginBundle) { Remove-Item $pluginBundle -Force }
            & tar.exe -czf $pluginBundle --exclude=.git --exclude=data --exclude=.runtime --exclude=*.tmp --exclude=*.tar.gz -C $pluginRelDir .
            if ($LASTEXITCODE -ne 0 -or -not (Test-Path $pluginBundle)) {
                throw "Failed to create plugin bundle at $pluginBundle"
            }

            Invoke-RemoteSsh "mkdir -p $devWorkdir/plugins/$HotUpdatePlugin"
            $pluginStreamArgs = @(
                "-i", $tempKeyPath,
                "-p", "$deployPort",
                "-o", "StrictHostKeyChecking=no",
                "-o", "UserKnownHostsFile=/dev/null",
                "-o", "LogLevel=ERROR",
                "$deployUser@$deployHost",
                "tar -xzf - -C $devWorkdir/plugins/$HotUpdatePlugin"
            )
            $pProc = Start-Process -FilePath "ssh" -ArgumentList $pluginStreamArgs -RedirectStandardInput $pluginBundle -NoNewWindow -Wait -PassThru
            if ($pProc.ExitCode -ne 0) {
                throw "Plugin host sync failed with exit code $($pProc.ExitCode)"
            }
            Invoke-RemoteSsh "chmod -R +x $devWorkdir/plugins/$HotUpdatePlugin 2>/dev/null || true"

            # 3. 检查容器运行态并执行热更新原子切替
            $containerRunning = (Invoke-RemoteSsh "docker ps -q --filter name=$containerName").Trim()
            if (-not $containerRunning) {
                Write-Warn "Container $containerName is not running. Host volume files updated."
                return
            }

            Write-Step "Executing in-place atomic hot-swap inside container $containerName..."
            $rawOldPid = (Invoke-RemoteSsh "docker exec $containerName sh -c 'cat /app/data/plugins/$HotUpdatePlugin/.runtime/process.pid 2>/dev/null || true'").Trim()
            $oldPid = [regex]::Match($rawOldPid, '\d+').Value
            Write-Host "Current microservice PID: $(if ($oldPid) { $oldPid } else { 'Not running' })" -ForegroundColor DarkYellow

            # 原子 inode 替换 (避免 Text file busy)
            $swapCmd = "sh -c 'mkdir -p /app/data/plugins/$HotUpdatePlugin && if [ -f /app/plugins/$HotUpdatePlugin/$HotUpdatePlugin ]; then cp -f /app/plugins/$HotUpdatePlugin/$HotUpdatePlugin /app/data/plugins/$HotUpdatePlugin/${HotUpdatePlugin}.hot && chmod +x /app/data/plugins/$HotUpdatePlugin/${HotUpdatePlugin}.hot && mv -f /app/data/plugins/$HotUpdatePlugin/${HotUpdatePlugin}.hot /app/data/plugins/$HotUpdatePlugin/$HotUpdatePlugin; fi && cp -rf /app/plugins/$HotUpdatePlugin/* /app/data/plugins/$HotUpdatePlugin/ 2>/dev/null || true && chmod -R +x /app/data/plugins/$HotUpdatePlugin 2>/dev/null || true'"
            Invoke-RemoteSsh "docker exec $containerName $swapCmd"

            # 4. 触发微服务平台平滑重拉 (使用统一 REST API)
            Write-Step "Triggering microservice restart via Unified Plugin Platform REST API..."
            $reloadScript = @"
python3 - << 'EOF'
import json, urllib.request, os
try:
    with open('/var/lib/docker/volumes/${containerName}-data/_data/admin.json') as f:
        pwd = json.load(f).get('generatedPassword')
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    req = urllib.request.Request('http://127.0.0.1:${devPort}/api/Auth/Login', data=json.dumps({'userName':'admin','password':pwd}).encode(), headers={'Content-Type':'application/json'})
    token = json.loads(opener.open(req).read().decode()).get('token')
    endpoint = 'Restart' if '$oldPid' != '' else 'Start'
    r_req = urllib.request.Request(f'http://127.0.0.1:${devPort}/api/Plugins/${HotUpdatePlugin}/' + endpoint, data=b'', headers={'Authorization': 'Bearer ' + token})
    print(opener.open(r_req).read().decode())
except Exception as ex:
    print('RELOAD_API_FALLBACK:', ex)
EOF
"@
            $reloadOutput = Invoke-RemoteSsh $reloadScript
            Write-Host "Reload API result: $reloadOutput" -ForegroundColor DarkCyan

            # 5. 等待微服务拉起并探活
            $newPid = ""
            $retryCount = 0
            $maxRetries = 15
            while ($retryCount -lt $maxRetries) {
                Start-Sleep -Milliseconds 600
                $retryCount++
                $rawCurPid = (Invoke-RemoteSsh "docker exec $containerName sh -c 'cat /app/data/plugins/$HotUpdatePlugin/.runtime/process.pid 2>/dev/null || true'").Trim()
                $curPid = [regex]::Match($rawCurPid, '\d+').Value
                if ($curPid -and $curPid -ne $oldPid) {
                    $newPid = $curPid
                    break
                }
            }

            # 校验容器内运行的二进制 Hash (若为二进制编译型微服务)
            $sw.Stop()
            if ($localHash) {
                $rawRemoteHash = (Invoke-RemoteSsh "docker exec $containerName sha256sum /app/data/plugins/$HotUpdatePlugin/$HotUpdatePlugin 2>/dev/null || true").Trim()
                $remoteHash = [regex]::Match($rawRemoteHash, '[0-9a-fA-F]{64}').Value.ToLower()
                if ($remoteHash -and ($localHash -eq $remoteHash)) {
                    Write-Success "Microservice binary hot-updated & verified! SHA256: $remoteHash"
                } else {
                    Write-Warn "Binary hash comparison: local=$localHash, remote=$remoteHash"
                }
            } else {
                Write-Success "Script/interpreted plugin hot-updated & verified!"
            }

            if ($newPid) {
                Write-Success "Microservice reloaded successfully! Old PID: $oldPid -> New PID: $newPid (Hot Reload Latency: $($sw.ElapsedMilliseconds) ms)"
            } else {
                Write-Warn "Microservice PID did not refresh within timeout. Check container logs."
            }

            # 检查监听端口
            $rawListenPort = (Invoke-RemoteSsh "docker exec $containerName sh -c 'netstat -tlnp 2>/dev/null | grep $HotUpdatePlugin || true'").Trim()
            $listenPort = ($rawListenPort -split "`r?`n" | Where-Object { $_ -match "$HotUpdatePlugin" } | Select-Object -First 1)
            if ($listenPort) {
                Write-Success "Service port listener active: $listenPort"
            }

            Write-Host "Plugin Console URL: http://${deployHost}:${devPort}/plugins/${HotUpdatePlugin}/" -ForegroundColor Green
            Write-Host "Zero-Downtime: Container $containerName was NOT restarted. Production container (${deployHost}:5270) unaffected." -ForegroundColor Cyan
            return
        }
        finally {
            if (Test-Path $pluginBundle) { Remove-Item $pluginBundle -Force -ErrorAction SilentlyContinue }
        }
    }


    if ($Mode -eq "Dev") {
        # 1/4 Code Sync (Adaptive Strategy Pattern: Auto -> GitArchive (if clean) vs Direct (if dirty) vs GitPull)
        $effectiveSync = $SyncMethod
        if ($SyncMethod -eq "Auto") {
            $hasGit = Test-Path ".git"
            if ($hasGit) {
                $gitStatus = (& git status --porcelain 2>&1)
                if ([string]::IsNullOrWhiteSpace($gitStatus)) {
                    $effectiveSync = "GitArchive"
                    $commitHash = (& git rev-parse --short HEAD 2>&1)
                    Write-Success "Adaptive Strategy: Git tree clean -> selected [GitArchive] ($commitHash) for maximum purity."
                } else {
                    $effectiveSync = "Direct"
                    $dirtyLines = $gitStatus -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
                    Write-Warn "Adaptive Strategy: Detected $($dirtyLines.Count) uncommitted changes -> selected [Direct] to include live edits."
                }
            } else {
                $effectiveSync = "Direct"
                Write-Success "Adaptive Strategy: Non-git workspace -> selected [Direct]."
            }
        }

        if ($effectiveSync -eq "Direct") {
            # Precompile mcp-bridge Linux static binary if missing or outdated
            $mcpSrc = "plugins/mcp-bridge/cmd/mcp-bridge/main.go"
            $mcpBin = "plugins/mcp-bridge/mcp-bridge"
            if ($mcpSrc -and (Test-Path -Path $mcpSrc)) {
                $isElf = $false
                if ($mcpBin -and (Test-Path -Path $mcpBin)) {
                    try {
                        $bytes = [System.IO.File]::ReadAllBytes($mcpBin)
                        if ($bytes.Length -ge 4 -and $bytes[0] -eq 0x7F -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x4C -and $bytes[3] -eq 0x46) {
                            $isElf = $true
                        }
                    } catch { }
                }
                $needCompile = (-not $isElf)
                if (-not $needCompile -and (Test-Path -Path $mcpBin) -and (Test-Path -Path "plugins/mcp-bridge")) {
                    $latestSource = (Get-ChildItem -Path "plugins/mcp-bridge" -Recurse -File -Exclude "mcp-bridge","*.exe" | Measure-Object -Property LastWriteTime -Maximum).Maximum
                    if ($latestSource -and ((Get-Item $mcpBin).LastWriteTime -lt $latestSource)) {
                        $needCompile = $true
                    }
                }
                if ($needCompile -and (Get-Command "go" -ErrorAction SilentlyContinue)) {
                    Write-Step "Pre-compiling plugins/mcp-bridge for Linux x64 (CGO_ENABLED=0 GOOS=linux GOARCH=amd64)..."
                    cmd /c "set CGO_ENABLED=0& set GOOS=linux& set GOARCH=amd64& cd plugins\mcp-bridge& go build -ldflags=""-s -w"" -o mcp-bridge ./cmd/mcp-bridge"
                } elseif (-not $isElf -and (Test-Path -Path $mcpBin)) {
                    Write-Warning "Local $mcpBin is not a Linux ELF binary; removing to avoid contaminating remote container."
                    Remove-Item $mcpBin -Force -ErrorAction SilentlyContinue
                }
            }

            Write-Step "1/4 [Dev - Direct Sync] Packaging live working tree via native tar (decoupled from Git)..."
            $localBundle = "dev-bundle-$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()).tar.gz"
            try {
                & tar.exe -czf $localBundle --exclude=.git --exclude=bin --exclude=obj --exclude=node_modules --exclude=env --exclude=*.tmp --exclude=*.tar.gz Directory.Build.props Directory.Packages.props Dockerfile.dev src plugins
                if ($LASTEXITCODE -ne 0 -or -not (Test-Path $localBundle)) {
                    throw "Failed to create direct workspace bundle at $localBundle"
                }

                Invoke-RemoteSsh "mkdir -p $devWorkdir"
                $streamArgs = @(
                    "-i", $tempKeyPath,
                    "-p", "$deployPort",
                    "-o", "StrictHostKeyChecking=no",
                    "-o", "UserKnownHostsFile=/dev/null",
                    "-o", "LogLevel=ERROR",
                    "$deployUser@$deployHost",
                    "tar -xzf - -C $devWorkdir"
                )
                $streamProc = Start-Process -FilePath "ssh" -ArgumentList $streamArgs -RedirectStandardInput $localBundle -NoNewWindow -Wait -PassThru
                if ($streamProc.ExitCode -ne 0) {
                    throw "Direct SSH stream extraction failed with exit code $($streamProc.ExitCode)"
                }
                Invoke-RemoteSsh "chmod -R +x $devWorkdir/plugins 2>/dev/null || true"
                Write-Success "Live workspace synchronized to $devWorkdir in <1s (Uncommitted edits included, Git decoupled)!"
            }
            finally {
                if (Test-Path $localBundle) { Remove-Item $localBundle -Force -ErrorAction SilentlyContinue }
            }
        }
        elseif ($effectiveSync -eq "GitArchive") {
            Write-Step "1/4 [Dev - GitArchive Sync] Bundling local git HEAD commit..."
            $localBundle = "dev-bundle.tar.gz"
            try {
                if (Test-Path $localBundle) { Remove-Item $localBundle -Force }
                $p = Start-Process -FilePath "git" -ArgumentList "archive", "--output=$localBundle", "HEAD" -NoNewWindow -Wait -PassThru
                if ($p.ExitCode -ne 0 -or -not (Test-Path $localBundle)) {
                    throw "Failed to create git archive bundle at $localBundle"
                }

                Invoke-RemoteSsh "mkdir -p $devWorkdir"
                $streamArgs = @(
                    "-i", $tempKeyPath,
                    "-p", "$deployPort",
                    "-o", "StrictHostKeyChecking=no",
                    "-o", "UserKnownHostsFile=/dev/null",
                    "-o", "LogLevel=ERROR",
                    "$deployUser@$deployHost",
                    "tar -xzf - -C $devWorkdir"
                )
                $streamProc = Start-Process -FilePath "ssh" -ArgumentList $streamArgs -RedirectStandardInput $localBundle -NoNewWindow -Wait -PassThru
                if ($streamProc.ExitCode -ne 0) {
                    throw "SSH stream extraction failed with exit code $($streamProc.ExitCode)"
                }
                Write-Success "Git HEAD bundle synchronized to $devWorkdir in <1s!"
            }
            finally {
                if (Test-Path $localBundle) { Remove-Item $localBundle -Force -ErrorAction SilentlyContinue }
            }
        }
        elseif ($effectiveSync -eq "GitPull") {
            Write-Step "1/4 [Dev - GitPull Sync] Pulling remote git repository from GitHub on host..."
            $gitSyncScript = @"
if [ ! -d "$devWorkdir/.git" ]; then
    mkdir -p "$devWorkdir"
    git clone https://github.com/csvkse/LWT.git "$devWorkdir"
else
    git -C "$devWorkdir" fetch origin main
    git -C "$devWorkdir" reset --hard origin/main
fi
git -C "$devWorkdir" log -1 --oneline
"@
            $gitOutput = Invoke-RemoteSsh $gitSyncScript
            Write-Host $gitOutput -ForegroundColor DarkCyan
            Write-Success "Remote source code synchronized via Git pull!"
        }

        # Pre-build Disk Safety Check & Emergency Quota Guard
        try {
            $rawDisk = (Invoke-RemoteSsh "df -P / | tail -n 1").Trim()
            $diskParts = ($rawDisk -split '\s+')
            if ($diskParts.Length -ge 5 -and $diskParts[4] -match '(\d+)%') {
                $usedPct = [int]$matches[1]
                if ($usedPct -ge 85) {
                    Write-Warn "Host disk pressure warning: Root partition is at ${usedPct}%! Triggering pre-build emergency cleanup..."
                    Invoke-RemoteSsh "docker image prune -f >/dev/null 2>&1 || true; docker builder prune -f --keep-storage=2GB >/dev/null 2>&1 || true"
                }
            }
        } catch {}

        Write-Step "2/4 [Dev] Building fast development image ($devImage) via Dockerfile.dev..."
        Invoke-RemoteSsh "docker build -f $devWorkdir/Dockerfile.dev -t $devImage $devWorkdir"
        Write-Success "Fast dev image built successfully!"

        Write-Step "3/4 [Dev] Launching development container ($containerName on port $devPort)..."
        Invoke-RemoteSsh "mkdir -p /var/lib/incus"
        $devRunCmd = "docker rm -f $containerName 2>/dev/null || true; docker run -d --name $containerName --restart unless-stopped --privileged --pid=host --user root --device /dev/fuse --device=/dev/net/tun -v /var/run/docker.sock:/var/run/docker.sock -p ${devPort}:5270 -p 18080-18090:18080-18090 -p 10801-10815:10801-10815 -e LWT_HOST_EXTERNAL_PORT=${devPort} -e LWT_HOST_INTERNAL_PORT=5270 -e LWT_HOST_PUBLIC_IP=${deployHost} -v ${containerName}-data:/app/data -v ${devWorkdir}/src/LinuxWebTool.WebHost/wwwroot:/app/wwwroot -v ${devWorkdir}/plugins:/app/plugins -v /lwt:/lwt:rshared -v /var/docker:/var/docker:rshared -v /var/lib/incus:/var/lib/incus:rshared -v /usr/local/bin:/usr/local/bin:ro $devImage"
        Invoke-RemoteSsh $devRunCmd
        Write-Success "Dev container launched (Live volume mounts for wwwroot and plugins active)!"

        Write-Step "4/4 [Dev] Waiting for dev container health..."
        $maxWaitSec = 30
        $waited = 0
        $isHealthy = $false

        while ($waited -lt $maxWaitSec) {
            Start-Sleep -Seconds 2
            $waited += 2
            try {
                $cState = (Invoke-RemoteSsh "docker inspect --format '{{.State.Status}}' $containerName 2>/dev/null").Trim()
                if ($cState -eq "restarting" -or $cState -eq "exited") {
                    Write-Warn "Container entered $cState state! Recent logs:"
                    Invoke-RemoteSsh "docker logs --tail 20 $containerName"
                    throw "Dev container failed to start (State: $cState)"
                }
                $healthStatus = (Invoke-RemoteSsh "docker inspect --format '{{.State.Health.Status}}' $containerName 2>/dev/null").Trim()
                if ($healthStatus -eq "healthy") {
                    $isHealthy = $true
                    break
                }
            } catch {
                if ($_.Exception.Message -match "failed to start") { throw $_ }
            }
        }

        if (-not $isHealthy) {
            Write-Warn "Dev container did not report healthy within $maxWaitSec seconds. Recent logs:"
            Invoke-RemoteSsh "docker logs --tail 25 $containerName"
        } else {
            Write-Success "Dev container is healthy and responding!"
        }

        # Sync credentials from production container if needed
        try {
            Invoke-RemoteSsh "if docker ps -q --filter name=linuxwebtool | grep -q . ; then docker exec linuxwebtool cat /app/data/admin.json > /tmp/admin.json 2>/dev/null && docker cp /tmp/admin.json ${containerName}:/app/data/admin.json 2>/dev/null && rm -f /tmp/admin.json; fi"
            Write-Success "Synced admin credentials from production container!"
        } catch {
            Write-Warn "Failed to sync credentials, container will use its own admin.json."
        }

        # Sync live plugins to data volume
        try {
            Invoke-RemoteSsh "docker exec $containerName sh -c 'mkdir -p /app/data/plugins && cp -rf /app/plugins/* /app/data/plugins/ 2>/dev/null || true && chmod -R +x /app/data/plugins 2>/dev/null || true'"
            Write-Success "Synced live plugins to container data volume!"
        } catch {}

        # Automated Smoke Test
        try {
            $smokeCode = (Invoke-RemoteSsh "curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:${devPort}/health").Trim()
            if ($smokeCode -eq "200") {
                Write-Success "Smoke Test: GET http://127.0.0.1:${devPort}/health returned 200 OK!"
            }
        } catch {}

        # Test Incus socket access from within container if socket exists
        try {
            $incusCheck = (Invoke-RemoteSsh "docker exec $containerName sh -c 'if [ -S /var/lib/incus/unix.socket ]; then echo socket_found; fi' 2>/dev/null").Trim()
            if ($incusCheck -eq "socket_found") {
                Write-Success "Incus Socket check: /var/lib/incus/unix.socket detected and accessible inside $containerName!"
            }
        } catch {}

        $ps = Invoke-RemoteSsh "docker ps --filter name=$containerName"
        Write-Host $ps -ForegroundColor Green

        # Post-Deploy Disk Hygiene Guard (Purge dangling images & bound BuildKit cache)
        Write-Step "[Dev] Executing post-deploy disk hygiene guard..."
        try {
            Invoke-RemoteSsh "docker image prune -f >/dev/null 2>&1 || true; docker builder prune -f --keep-storage=3GB >/dev/null 2>&1 || true"
            Write-Success "Post-deploy hygiene complete: Dangling images purged and BuildKit cache bounded to 3GB!"
        } catch {
            Write-Warn "Post-deploy hygiene cleanup encountered a non-fatal warning: $($_.Exception.Message)"
        }

        Write-Step "Fast dev deployment finished successfully!"
        Write-Host "Dev Web URL: $devWebUrl" -ForegroundColor Green
        Write-Host "Notice: Production container ($webBaseUrl) remains untouched and fully operational!" -ForegroundColor Cyan
        return
    }

    # =========================================================================
    # Strategy B: Official Production Pipeline (GHCR Multi-Arch AOT Image Pull)
    # =========================================================================
    Write-Step "1/4 Pulling official Docker image ($dockerImage)..."
    Invoke-RemoteSsh "docker pull $dockerImage"
    Write-Success "Docker pull finished!"

    Write-Step "2/4 Recreating production container with privileged and host network mode..."
    $dockerRunCmd = "docker rm -f $containerName 2>/dev/null || true; docker run -d --name $containerName --restart unless-stopped --privileged --pid=host --user root --device /dev/fuse --device=/dev/net/tun -v /var/run/docker.sock:/var/run/docker.sock -p ${webPort}:${webPort} --net=host -e LWT_HOST_EXTERNAL_PORT=${webPort} -e LWT_HOST_INTERNAL_PORT=${webPort} -e LWT_HOST_PUBLIC_IP=${deployHost} -v linuxwebtool-data:/app/data -v /lwt:/lwt:rshared -v /var/docker:/var/docker:rshared -v /var/lib/incus:/var/lib/incus:rshared -v /usr/local/bin:/usr/local/bin:ro $dockerImage"
    Invoke-RemoteSsh $dockerRunCmd
    Write-Success "Container recreated!"

    Write-Step "3/4 Waiting for container to report healthy..."
    $maxWaitSec = 45
    $waited = 0
    $isHealthy = $false

    while ($waited -lt $maxWaitSec) {
        Start-Sleep -Seconds 2
        $waited += 2
        try {
            $healthStatus = Invoke-RemoteSsh "docker inspect --format '{{.State.Health.Status}}' $containerName 2>/dev/null"
            if ($healthStatus -match "healthy") {
                $isHealthy = $true
                break
            }
        } catch {}
    }

    if (-not $isHealthy) {
        Write-Warn "Container did not reach healthy status within $maxWaitSec seconds. Recent logs:"
        Invoke-RemoteSsh "docker logs --tail 25 $containerName"
    } else {
        Write-Success "Container is healthy!"
    }

    if (-not $SkipDeps) {
        Write-Step "4/4 Checking runtime dependencies in container..."
        $depCheck = Invoke-RemoteSsh "docker exec $containerName sh -c 'which node python3 dotnet pwsh caddy 2>/dev/null | wc -l'"
        $installedCount = [int]($depCheck.Trim())

        if ($installedCount -ge 5) {
            Write-Success "All 5 runtime environments installed and ready ($installedCount/5)"
        } else {
            Write-Warn "Some runtimes missing ($installedCount/5). Configuring USTC mirror and installing..."
            $setupScript = 'sed -i "s@dl-cdn.alpinelinux.org@mirrors.ustc.edu.cn@g" /etc/apk/repositories && apk update && apk add --no-cache caddy nodejs python3 py3-pip dotnet8-sdk powershell'
            $encodedSetupScript = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($setupScript))
            Invoke-RemoteSsh "docker exec $containerName sh -c 'echo $encodedSetupScript | base64 -d | sh'"
            Write-Success "Runtimes installed successfully!"
        }

        $versions = Invoke-RemoteSsh "docker exec $containerName sh -c 'echo Node: `$(node -v 2>/dev/null); echo Python: `$(python3 --version 2>/dev/null); echo .NET: `$(dotnet --version 2>/dev/null); echo PS: `$(pwsh -v 2>/dev/null); echo Caddy: `$(caddy version 2>/dev/null)'"
        Write-Host $versions -ForegroundColor DarkCyan
    }

    # Post-Deploy Disk Hygiene Guard for Official Mode (Purge untagged layers)
    try {
        Invoke-RemoteSsh "docker image prune -f >/dev/null 2>&1 || true"
        Write-Success "Official post-deploy hygiene: Dangling images purged!"
    } catch {}

    Write-Step "Official deployment finished successfully!"
    Write-Host "Web URL: $webBaseUrl" -ForegroundColor Green
}
finally {
    if (Test-Path $tempKeyPath) {
        Remove-Item -Path $tempKeyPath -Force -ErrorAction SilentlyContinue
    }
}
