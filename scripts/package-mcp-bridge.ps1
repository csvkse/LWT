<#
.SYNOPSIS
    Packages the Go MCP Bridge worker into standalone distribution archives for Linux and Windows.
#>
param(
    [string]$OutputDir = "$PSScriptRoot\..\artifacts\mcp-bridge"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$workersBridgeDir = "$PSScriptRoot\..\workers\mcp-bridge"
Push-Location $workersBridgeDir

try {
    $targets = @(
        @{ OS = "windows"; Arch = "amd64"; Name = "win-x64"; Binary = "mcp-bridge.exe" },
        @{ OS = "linux"; Arch = "amd64"; Name = "linux-x64"; Binary = "mcp-bridge" },
        @{ OS = "linux"; Arch = "arm64"; Name = "linux-arm64"; Binary = "mcp-bridge" }
    )

    $hashes = @()

    foreach ($t in $targets) {
        Write-Host "Building mcp-bridge for $($t.OS)/$($t.Arch)..."
        $env:GOOS = $t.OS
        $env:GOARCH = $t.Arch
        $env:CGO_ENABLED = "0"

        $stagingDir = "$OutputDir\staging-$($t.Name)"
        if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
        New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

        $binPath = "$stagingDir\$($t.Binary)"
        go build -ldflags "-s -w" -o $binPath ./cmd/mcp-bridge

        # Copy manifest
        $manifest = Get-Content "$workersBridgeDir\packaging\worker.json" -Raw
        if ($t.OS -eq "windows") {
            $manifest = $manifest.Replace('"entrypoint": "mcp-bridge"', '"entrypoint": "mcp-bridge.exe"')
        }
        Set-Content -Path "$stagingDir\worker.json" -Value $manifest -Encoding utf8

        # Copy docs & examples
        Copy-Item "$workersBridgeDir\packaging\bridge.example.json" "$stagingDir\" -Force
        Copy-Item "$workersBridgeDir\README.md" "$stagingDir\" -Force

        # Create zip
        $zipPath = "$OutputDir\mcp-bridge-$($t.Name).zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path "$stagingDir\*" -DestinationPath $zipPath -Force
        Remove-Item $stagingDir -Recurse -Force

        $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLower()
        $zipName = [System.IO.Path]::GetFileName($zipPath)
        $hashes += "$hash  $zipName"
        Write-Host "Created $zipPath (SHA256: $hash)"
    }

    Set-Content -Path "$OutputDir\SHA256SUMS" -Value ($hashes -join "`n") -Encoding utf8
    Write-Host "Packaging completed successfully. Artifacts in $OutputDir"
}
finally {
    Pop-Location
    $env:GOOS = ""
    $env:GOARCH = ""
    $env:CGO_ENABLED = ""
}
