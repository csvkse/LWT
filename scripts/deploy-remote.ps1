[CmdletBinding()]
param(
    [ValidateSet("Official", "Dev")]
    [string]$Mode = "Official",

    [string]$EnvFile = "env/.envdev",
    [switch]$SkipDeps,
    [switch]$CheckOnly
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
if ($Mode -eq "Dev") {
    $containerName = "${containerName}-dev"
    $devPort = "5271"
    $devImage = "lwt:dev"
    $devWorkdir = "/opt/lwt-dev"
    $devWebUrl = "http://${deployHost}:${devPort}/"
    Write-Success "Adapter [LinuxFastDevAdapter] selected: Target $deployUser@$deployHost`:$deployPort, container $containerName (Port $devPort)"
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
    # Strategy A: Linux Fast Dev Pipeline (Git Pull + Local Fast JIT Docker Build)
    # =========================================================================
    if ($Mode -eq "Dev") {
        Write-Step "1/4 [Dev] Syncing repository to $devWorkdir on remote host..."
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
        Write-Success "Source code synchronized!"

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
  -v ${containerName}-data:/app/data \
  -v /lwt:/lwt:rshared \
  -v /var/docker:/var/docker:rshared \
  -v /usr/local/bin:/usr/local/bin:ro \
  $devImage
"@
        Invoke-RemoteSsh $devRunCmd
        Write-Success "Dev container launched!"

        Write-Step "4/4 [Dev] Waiting for dev container health..."
        Start-Sleep -Seconds 3
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
