# LinuxWebTool 快速门禁：编译 + 架构测试 + 单元测试 + 集成测试 + 前端门禁与单元测试 + 冒烟工具回归
# 用法：./scripts/verify-fast.ps1  或  ./scripts/verify-fast.ps1 -SkipFrontend
param(
    [switch]$SkipFrontend
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host '== [1/6] dotnet build ==' -ForegroundColor Cyan
dotnet build (Join-Path $root 'LinuxWebTool.slnx') -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw '编译失败' }

Write-Host '== [2/6] dotnet test (架构门禁) ==' -ForegroundColor Cyan
dotnet test (Join-Path $root 'tests/LinuxWebTool.ArchitectureTests/LinuxWebTool.ArchitectureTests.csproj') -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw '架构门禁测试失败' }

Write-Host '== [3/6] dotnet test (白盒单元测试) ==' -ForegroundColor Cyan
dotnet test (Join-Path $root 'tests/LinuxWebTool.UnitTests/LinuxWebTool.UnitTests.csproj') -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw '单元测试失败' }

Write-Host '== [4/6] dotnet test (API 集成门禁) ==' -ForegroundColor Cyan
dotnet test (Join-Path $root 'tests/LinuxWebTool.IntegrationTests/LinuxWebTool.IntegrationTests.csproj') -c Release --no-build --nologo --filter 'Category!=External'
if ($LASTEXITCODE -ne 0) { throw 'API 集成测试失败' }

if (-not $SkipFrontend) {
    Write-Host '== [5/6] 前端架构门禁与前端单元测试 ==' -ForegroundColor Cyan
    $wwwroot = Join-Path $root 'src/LinuxWebTool.WebHost/wwwroot'
    node (Join-Path $wwwroot 'frontend-gate.cjs')
    if ($LASTEXITCODE -ne 0) { throw '前端架构门禁失败' }

    $frontendTests = (Get-ChildItem (Join-Path $root 'tests/frontend/*.test.mjs')).FullName
    if ($frontendTests.Count -gt 0) {
        node --experimental-vm-modules --test $frontendTests
        if ($LASTEXITCODE -ne 0) { throw '前端单元测试失败' }
    }

    if (Test-Path (Join-Path $wwwroot 'node_modules')) {
        Push-Location $wwwroot
        npm run lint
        Pop-Location
        if ($LASTEXITCODE -ne 0) { throw 'ESLint 失败' }
    } else {
        Write-Host '（未安装 wwwroot/node_modules，跳过 ESLint；如需 lint 请执行 npm install）' -ForegroundColor Yellow
    }
}

Write-Host '== [6/6] 冒烟与性能测试套件回归 ==' -ForegroundColor Cyan
node --test (Join-Path $root 'tests/smoke/http-contract.test.mjs') (Join-Path $root 'tests/smoke/performance.test.mjs')
if ($LASTEXITCODE -ne 0) { throw '冒烟工具回归测试失败' }

Write-Host "`n✅ 快速门禁全部通过" -ForegroundColor Green
