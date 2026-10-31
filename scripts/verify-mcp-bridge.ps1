<#
.SYNOPSIS
    Runs full verification suite for MCP Bridge (Go tests, .NET tests, frontend gate).
#>
$ErrorActionPreference = "Stop"

Write-Host "=== 1. Verifying Go MCP Bridge Service ===" -ForegroundColor Cyan
Push-Location "$PSScriptRoot\..\workers\mcp-bridge"
try {
    go test ./... -count=1
    if ($LASTEXITCODE -ne 0) { throw "Go unit tests failed" }
}
finally {
    Pop-Location
}

Write-Host "=== 2. Verifying .NET Unit Tests ===" -ForegroundColor Cyan
dotnet test "$PSScriptRoot\..\tests\LinuxWebTool.UnitTests\LinuxWebTool.UnitTests.csproj" --filter "FullyQualifiedName~NativeWorkerAdapterTests|FullyQualifiedName~ResidentControlClientTests|FullyQualifiedName~McpBridge"
if ($LASTEXITCODE -ne 0) { throw ".NET Unit tests failed" }

Write-Host "=== 3. Verifying Architecture Gates ===" -ForegroundColor Cyan
dotnet test "$PSScriptRoot\..\tests\LinuxWebTool.ArchitectureTests\LinuxWebTool.ArchitectureTests.csproj"
if ($LASTEXITCODE -ne 0) { throw "Architecture tests failed" }

Write-Host "=== 4. Verifying Frontend Gate ===" -ForegroundColor Cyan
node "$PSScriptRoot\..\src\LinuxWebTool.WebHost\wwwroot\frontend-gate.cjs"
if ($LASTEXITCODE -ne 0) { throw "Frontend gate failed" }

Write-Host "=== 5. Verifying Frontend Unit Tests ===" -ForegroundColor Cyan
node --test "$PSScriptRoot\..\tests\frontend\*.test.mjs"
if ($LASTEXITCODE -ne 0) { throw "Frontend unit tests failed" }

Write-Host "=== ALL VERIFICATIONS PASSED ===" -ForegroundColor Green
