# LinuxWebTool 快速门禁：编译 + 架构测试 + 前端门禁
# 用法：./scripts/verify-fast.ps1  或  ./scripts/verify-fast.ps1 -SkipFrontend
param(
    [switch]$SkipFrontend
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host '== [1/3] dotnet build ==' -ForegroundColor Cyan
dotnet build (Join-Path $root 'LinuxWebTool.slnx') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw '编译失败' }

Write-Host '== [2/3] dotnet test (架构门禁) ==' -ForegroundColor Cyan
dotnet test (Join-Path $root 'tests/LinuxWebTool.ArchitectureTests/LinuxWebTool.ArchitectureTests.csproj') -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw '架构门禁测试失败' }

if (-not $SkipFrontend) {
    Write-Host '== [3/3] 前端架构门禁 ==' -ForegroundColor Cyan
    $wwwroot = Join-Path $root 'src/LinuxWebTool.WebHost/wwwroot'
    node (Join-Path $wwwroot 'frontend-gate.cjs')
    if ($LASTEXITCODE -ne 0) { throw '前端架构门禁失败' }

    if (Test-Path (Join-Path $wwwroot 'node_modules')) {
        Push-Location $wwwroot
        npm run lint
        Pop-Location
        if ($LASTEXITCODE -ne 0) { throw 'ESLint 失败' }
    } else {
        Write-Host '（未安装 wwwroot/node_modules，跳过 ESLint；如需 lint 请执行 npm install）' -ForegroundColor Yellow
    }
}

Write-Host "`n✅ 快速门禁全部通过" -ForegroundColor Green
