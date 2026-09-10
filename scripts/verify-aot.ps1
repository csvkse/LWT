param(
    [string]$ImageTag = 'linuxwebtool:aot-verify'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host '== [1/3] 后端架构门禁 ==' -ForegroundColor Cyan
dotnet test (Join-Path $root 'tests/LinuxWebTool.ArchitectureTests/LinuxWebTool.ArchitectureTests.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '后端架构门禁失败' }

Write-Host '== [2/3] Linux NativeAOT Docker 构建 ==' -ForegroundColor Cyan
docker build --pull --tag $ImageTag $root
if ($LASTEXITCODE -ne 0) { throw 'Linux NativeAOT Docker 构建失败' }

Write-Host '== [3/3] NativeAOT Docker 接口冒烟 ==' -ForegroundColor Cyan
& (Join-Path $root 'scripts/smoke-aot.ps1') -ImageTag $ImageTag
if ($LASTEXITCODE -ne 0) { throw 'NativeAOT Docker 接口冒烟失败' }

Write-Host "`n✅ AOT 构建与接口验证完成：$ImageTag" -ForegroundColor Green
