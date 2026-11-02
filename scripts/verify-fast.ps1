# LinuxWebTool Fast Verification Gate
param(
    [switch]$SkipFrontend
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "== [0/7] Security Gate (Secret Leakage & Git Hooks) ==" -ForegroundColor Cyan
git -C $root config core.hooksPath .githooks
node (Join-Path $root ".githooks/security-gate.cjs") --full
if ($LASTEXITCODE -ne 0) { throw "Security gate failed" }

Write-Host "== [1/7] dotnet build ==" -ForegroundColor Cyan
dotnet build (Join-Path $root "LinuxWebTool.slnx") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

Write-Host "== [2/7] dotnet test (Architecture Gate) ==" -ForegroundColor Cyan
dotnet test (Join-Path $root "tests/LinuxWebTool.ArchitectureTests/LinuxWebTool.ArchitectureTests.csproj") -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw "Architecture tests failed" }

Write-Host "== [3/7] dotnet test (Unit Tests) ==" -ForegroundColor Cyan
dotnet test (Join-Path $root "tests/LinuxWebTool.UnitTests/LinuxWebTool.UnitTests.csproj") -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }

Write-Host "== [4/7] dotnet test (Integration Tests) ==" -ForegroundColor Cyan
dotnet test (Join-Path $root "tests/LinuxWebTool.IntegrationTests/LinuxWebTool.IntegrationTests.csproj") -c Release --no-build --nologo --filter "Category!=External"
if ($LASTEXITCODE -ne 0) { throw "Integration tests failed" }

if (-not $SkipFrontend) {
    Write-Host "== [5/7] Frontend Gate and Frontend Tests ==" -ForegroundColor Cyan
    $wwwroot = Join-Path $root "src/LinuxWebTool.WebHost/wwwroot"
    node (Join-Path $wwwroot "frontend-gate.cjs")
    if ($LASTEXITCODE -ne 0) { throw "Frontend gate failed" }

    $frontendTests = (Get-ChildItem (Join-Path $root "tests/frontend/*.test.mjs")).FullName
    if ($frontendTests.Count -gt 0) {
        node --experimental-vm-modules --test $frontendTests
        if ($LASTEXITCODE -ne 0) { throw "Frontend tests failed" }
    }
}

Write-Host "== [6/7] Smoke Tests ==" -ForegroundColor Cyan
node --test (Join-Path $root "tests/smoke/http-contract.test.mjs") (Join-Path $root "tests/smoke/performance.test.mjs")
if ($LASTEXITCODE -ne 0) { throw "Smoke tests failed" }

Write-Host "`nAll Fast Gate Verifications PASSED." -ForegroundColor Green
