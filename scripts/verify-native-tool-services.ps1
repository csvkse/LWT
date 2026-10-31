# Native Tool Services Complete Verification Suite
param(
    [switch]$SkipFrontend
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "== [1/5] Building .NET Solution (LinuxWebTool.slnx) ==" -ForegroundColor Cyan
dotnet build (Join-Path $repoRoot "LinuxWebTool.slnx") -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }

Write-Host "== [2/5] Running Architecture Gate Tests ==" -ForegroundColor Cyan
dotnet test (Join-Path $repoRoot "tests/LinuxWebTool.ArchitectureTests/LinuxWebTool.ArchitectureTests.csproj") -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw "Architecture tests failed" }

Write-Host "== [3/5] Running Unit Tests (SecurityGuard, Binding, Executor, Coordinator, Control) ==" -ForegroundColor Cyan
dotnet test (Join-Path $repoRoot "tests/LinuxWebTool.UnitTests/LinuxWebTool.UnitTests.csproj") -c Release --no-build --nologo
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed" }

if (-not $SkipFrontend) {
    Write-Host "== [4/5] Running Frontend Gate & Unit Tests ==" -ForegroundColor Cyan
    $wwwroot = Join-Path $repoRoot "src/LinuxWebTool.WebHost/wwwroot"
    node (Join-Path $wwwroot "frontend-gate.cjs")
    if ($LASTEXITCODE -ne 0) { throw "Frontend architecture gate failed" }

    $frontendTests = (Get-ChildItem (Join-Path $repoRoot "tests/frontend/*.test.mjs")).FullName
    if ($frontendTests.Count -gt 0) {
        node --experimental-vm-modules --test $frontendTests
        if ($LASTEXITCODE -ne 0) { throw "Frontend tests failed" }
    }
}

Write-Host "== [5/5] Packaging Native Tool Examples ==" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "package-native-tool-examples.ps1")
if ($LASTEXITCODE -ne 0) { throw "Packaging examples failed" }

Write-Host "`nAll Native Tool Services verifications PASSED successfully." -ForegroundColor Green
