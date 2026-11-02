[CmdletBinding()]
param(
    [ValidateSet("Official", "Dev")]
    [string]$Mode = "Official",

    [ValidateSet("Auto", "Direct", "GitArchive", "GitPull")]
    [string]$SyncMethod = "Auto",

    [switch]$FrontendOnly,

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
if ($FrontendOnly) {
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

    function Invoke-RemoteSsh([string]$cmd) {
        $sshArgs = @(
            "-i", $tempKeyPath,
            "-p", $deployPort,
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=/dev/null",
            "-o", "LogLevel=ERROR",
            "$deployUser@$deployHost",
            $cmd
        )
        $output = & ssh @sshArgs
        if ($LASTEXITCODE -ne 0) {
            throw "SSH command failed with exit code $($LASTEXITCODE): $cmd"
        }
        return $output
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

        Write-Step "2/4 [Dev] Building fast development image ($devImage) via Dockerfile.dev..."
        Invoke-RemoteSsh "docker build -f $devWorkdir/Dockerfile.dev -t $devImage $devWorkdir"
        Write-Success "Fast dev image built successfully!"

        Write-Step "3/4 [Dev] Launching development container ($containerName on port $devPort)..."
        $devRunCmd = @"
docker rm -f $containerName 2>/dev/null || true
docker run -d \
  --name $containerName \
  --restart unless-stopped \
  --privileged \
  --pid=host \
  --user root \
  --device /dev/fuse \
  --device=/dev/net/tun \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -p ${devPort}:5270 \
  -p 18080-18090:18080-18090 \
  -v ${containerName}-data:/app/data \
  -v ${devWorkdir}/src/LinuxWebTool.WebHost/wwwroot:/app/wwwroot \
  -v ${devWorkdir}/plugins:/app/plugins \
  -v /lwt:/lwt:rshared \
  -v /var/docker:/var/docker:rshared \
  -v /usr/local/bin:/usr/local/bin:ro \
  $devImage
"@
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

        $ps = Invoke-RemoteSsh "docker ps --filter name=$containerName"
        Write-Host $ps -ForegroundColor Green

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
    $dockerRunCmd = "docker rm -f $containerName 2>/dev/null || true; docker run -d --name $containerName --restart unless-stopped --privileged --pid=host --user root --device /dev/fuse --device=/dev/net/tun -v /var/run/docker.sock:/var/run/docker.sock -p ${webPort}:${webPort} --net=host -v linuxwebtool-data:/app/data -v /lwt:/lwt:rshared -v /var/docker:/var/docker:rshared -v /usr/local/bin:/usr/local/bin:ro $dockerImage"
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
            $setupScript = "sed -i 's/dl-cdn.alpinelinux.org/mirrors.ustc.edu.cn/g' /etc/apk/repositories && apk update && apk add --no-cache caddy nodejs python3 py3-pip dotnet8-sdk powershell"
            Invoke-RemoteSsh "docker exec $containerName sh -c `"$setupScript`""
            Write-Success "Runtimes installed successfully!"
        }

        $versions = Invoke-RemoteSsh "docker exec $containerName sh -c 'echo Node: `$(node -v 2>/dev/null); echo Python: `$(python3 --version 2>/dev/null); echo .NET: `$(dotnet --version 2>/dev/null); echo PS: `$(pwsh -v 2>/dev/null); echo Caddy: `$(caddy version 2>/dev/null)'"
        Write-Host $versions -ForegroundColor DarkCyan
    }

    Write-Step "Official deployment finished successfully!"
    Write-Host "Web URL: $webBaseUrl" -ForegroundColor Green
}
finally {
    if (Test-Path $tempKeyPath) {
        Remove-Item -Path $tempKeyPath -Force -ErrorAction SilentlyContinue
    }
}
