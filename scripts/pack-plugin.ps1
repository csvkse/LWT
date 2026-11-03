[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$PluginId,

    [string]$OutDir = "dist",
    [switch]$SkipBuild,
    [string]$ImportTo = "",
    [string]$EnvFile = "env/.envdev"
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

# 1. 定位插件目录
$pluginDir = $PluginId
if (-not (Test-Path $pluginDir)) {
    $candidate = Join-Path "plugins" $PluginId
    if (Test-Path $candidate) {
        $pluginDir = $candidate
    } else {
        throw "Plugin directory not found: '$PluginId' or '$candidate'"
    }
}

$pluginDir = (Resolve-Path $pluginDir).Path
Write-Step "Locating plugin directory: $pluginDir"

# 2. 校验 plugin.json 清单
$manifestPath = Join-Path $pluginDir "plugin.json"
if (-not (Test-Path $manifestPath)) {
    throw "Manifest file not found: $manifestPath"
}

$manifestRaw = Get-Content $manifestPath -Raw -Encoding UTF8
try {
    $manifest = $manifestRaw | ConvertFrom-Json
} catch {
    throw "plugin.json parsing failed: $($_.Exception.Message)"
}

$id = $manifest.id
if ([string]::IsNullOrWhiteSpace($id)) {
    $id = Split-Path $pluginDir -Leaf
}
$version = $manifest.version
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = "1.0.0"
}

Write-Success "Manifest valid: ID=[$id], Version=[$version], Profile=[$($manifest.profile)], Runtime=[$($manifest.runtime)]"

# 3. 换行符 LF 归一化清洗
Write-Step "Normalizing line endings (CRLF -> LF)..."
$textExtensions = @(".sh", ".json", ".js", ".mjs", ".html", ".css", ".md", ".txt", ".yml", ".yaml")
$textFiles = Get-ChildItem -Path $pluginDir -Recurse -File | Where-Object {
    $ext = $_.Extension.ToLower()
    $textExtensions -contains $ext -and $_.FullName -notmatch "node_modules|\.git|bin|obj"
}

$cleanedCount = 0
foreach ($tf in $textFiles) {
    $content = [System.IO.File]::ReadAllText($tf.FullName)
    if ($content.Contains("`r`n")) {
        $normalized = $content.Replace("`r`n", "`n")
        [System.IO.File]::WriteAllText($tf.FullName, $normalized, (New-Object System.Text.UTF8Encoding($false)))
        $cleanedCount++
    }
}
Write-Success "Line endings cleaned: normalized $cleanedCount text files to Unix LF."

# 4. 针对 Go 语言编译型微服务自动交叉编译 Linux ELF
$isGo = ($manifest.runtime -eq "go" -or $manifest.runtime -eq 6 -or (Test-Path (Join-Path $pluginDir "go.mod")))
if ($isGo -and -not $SkipBuild) {
    Write-Step "Go microservice detected. Cross-compiling Linux x64 static ELF binary..."
    $entrypoint = $manifest.entrypoint
    if ([string]::IsNullOrWhiteSpace($entrypoint)) {
        $entrypoint = "."
    }
    
    $buildTarget = $entrypoint
    if ($buildTarget.EndsWith(".go")) {
        $dirPart = Split-Path $buildTarget
        if ([string]::IsNullOrWhiteSpace($dirPart)) {
            $buildTarget = "."
        } else {
            $buildTarget = "./" + ($dirPart -replace '\\', '/')
        }
    }

    $outBinName = $id
    $outBinPath = Join-Path $pluginDir $outBinName

    Push-Location $pluginDir
    try {
        $env:CGO_ENABLED = "0"
        $env:GOOS = "linux"
        $env:GOARCH = "amd64"
        
        Write-Host "Running: go build -trimpath -ldflags '-s -w' -o $outBinName $buildTarget" -ForegroundColor Gray
        & go build -trimpath -ldflags "-s -w" -o $outBinName $buildTarget
        if ($LASTEXITCODE -ne 0) {
            throw "Go cross-compilation failed with exit code: $LASTEXITCODE"
        }
    } finally {
        Pop-Location
    }

    $binFileInfo = Get-Item $outBinPath
    $binSizeMb = [math]::Round($binFileInfo.Length / 1048576, 2)
    $binSha256 = (Get-FileHash -Path $outBinPath -Algorithm SHA256).Hash
    Write-Success "Linux ELF binary compiled! Size: $binSizeMb MB, SHA256: $binSha256"
}

# 5. 暂存与封包到标准 ZIP
Write-Step "Packaging into standard ZIP bundle..."
if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

$zipFileName = "${id}-${version}.zip"
$zipFilePath = Join-Path (Resolve-Path $OutDir).Path $zipFileName
if (Test-Path $zipFilePath) {
    Remove-Item $zipFilePath -Force
}

$stageDir = Join-Path ([System.IO.Path]::GetTempPath()) ("lwt_pack_" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stageDir -Force | Out-Null

try {
    $excludes = @(".git", "node_modules", "bin", "obj", "data.db", "kv.db", ".disabled", "*.log")
    
    Get-ChildItem -Path $pluginDir | ForEach-Object {
        $name = $_.Name
        $skip = $false
        foreach ($ex in $excludes) {
            if ($name -like $ex) { $skip = $true; break }
        }
        if (-not $skip) {
            Copy-Item -Path $_.FullName -Destination $stageDir -Recurse -Force
        }
    }

    Add-Type -AssemblyName "System.IO.Compression.FileSystem"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stageDir, $zipFilePath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    
    $zipInfo = Get-Item $zipFilePath
    $zipKb = [math]::Round($zipInfo.Length / 1024, 1)
    Write-Success "Bundle created: $zipFilePath ($zipKb KB)"
} finally {
    if (Test-Path $stageDir) {
        Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# 6. 一键自动导入目标环境 (若指定 -ImportTo)
if (-not [string]::IsNullOrWhiteSpace($ImportTo)) {
    Write-Step "Importing bundle directly to LWT platform at $ImportTo ..."
    
    $token = ""
    if (Test-Path $EnvFile) {
        $envLines = Get-Content $EnvFile
        $pwd = ""
        foreach ($l in $envLines) {
            if ($l -match '^WEB_ADMIN_PASSWORD=(.*)$') {
                $pwd = $matches[1].Trim('"', "'")
                break
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($pwd)) {
            try {
                $loginBody = @{ username = "admin"; password = $pwd } | ConvertTo-Json
                $loginRes = Invoke-RestMethod -Uri "$($ImportTo.TrimEnd('/'))/api/Auth/Login" -Method Post -Body $loginBody -ContentType "application/json"
                if ($loginRes.token) {
                    $token = $loginRes.token
                    Write-Success "Acquired admin authentication token"
                }
            } catch {
                Write-Warn "Automatic token acquisition failed, attempting unauthenticated upload..."
            }
        }
    }

    $uploadUri = "$($ImportTo.TrimEnd('/'))/api/Plugins/Import/Zip"
    
    $hasCurl = (Get-Command curl.exe -ErrorAction SilentlyContinue)
    if ($hasCurl) {
        $headers = @()
        if (-not [string]::IsNullOrWhiteSpace($token)) {
            $headers += "-H"
            $headers += "Authorization: Bearer $token"
        }
        
        Write-Host "Running: curl.exe -X POST $uploadUri -F 'file=@$zipFilePath'" -ForegroundColor Gray
        $curlArgs = @("-s", "-X", "POST", $uploadUri) + $headers + @("-F", "file=@$zipFilePath")
        $resJson = & curl.exe @curlArgs
        Write-Success "Import response: $resJson"
    } else {
        Write-Warn "curl.exe not found. You can upload $zipFilePath via Web UI at $($ImportTo.TrimEnd('/'))/app/#/plugins"
    }
}

Write-Host "`n====> [ALL DONE] Plugin '$id' packaged and ready!`n" -ForegroundColor Green
