param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [int]$Port = 15274,
    [switch]$Browser,
    [string]$Artifacts = 'artifacts/desktop-smoke'
)
$ErrorActionPreference = 'Stop'
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$evidence = Join-Path ([IO.Path]::GetFullPath($Artifacts)) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$data = Join-Path $evidence ('data-' + [guid]::NewGuid().ToString('N'))
$password = [guid]::NewGuid().ToString('N')
$saved = @{}
foreach ($key in @('ASPNETCORE_URLS', 'Urls', 'Admin__UserName', 'Admin__Password', 'Data__Directory', 'SMOKE_URL', 'SMOKE_USERNAME', 'SMOKE_PASSWORD', 'SMOKE_FILE_DIRECTORY', 'SMOKE_PERF_OUTPUT', 'SMOKE_TARGET', 'SMOKE_TARGET_MODE', 'SMOKE_LAUNCH_EPOCH_MS')) {
    $saved[$key] = [Environment]::GetEnvironmentVariable($key)
}
$process = $null
$resources = @{ schemaVersion = 1; source = 'process'; sampling = 'before/after smoke; not peak'; samples = @() }
function Get-SmokeResourceSample($phase) {
    try {
        $process.Refresh()
        return @{ phase = $phase; timestamp = [DateTimeOffset]::UtcNow.ToString('o'); cpuSeconds = $process.TotalProcessorTime.TotalSeconds; residentBytes = $process.WorkingSet64; privateBytes = $process.PrivateMemorySize64; threads = $process.Threads.Count }
    } catch { return @{ phase = $phase; unavailable = $true } }
}
try {
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
    $env:Urls = $env:ASPNETCORE_URLS
    $env:Admin__UserName = 'admin'
    $env:Admin__Password = $password
    $env:Data__Directory = $data
    $env:SMOKE_URL = $env:ASPNETCORE_URLS
    $env:SMOKE_USERNAME = 'admin'
    $env:SMOKE_PASSWORD = $password
    $env:SMOKE_FILE_DIRECTORY = [IO.Path]::GetTempPath()
    $env:SMOKE_PERF_OUTPUT = Join-Path $evidence 'performance.json'
    $env:SMOKE_TARGET = 'published-' + [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
    $env:SMOKE_TARGET_MODE = if (Test-Path -LiteralPath (Join-Path $publish 'LinuxWebTool.WebHost.dll')) { 'managed' } else { 'native-aot' }
    $executable = Join-Path $publish $(if ($IsWindows) { 'LinuxWebTool.WebHost.exe' } else { 'LinuxWebTool.WebHost' })
    $start = @{ FilePath = $executable; WorkingDirectory = $publish; PassThru = $true; RedirectStandardOutput = "$evidence/stdout.log"; RedirectStandardError = "$evidence/stderr.log" }
    if ($IsWindows) { $start.WindowStyle = 'Hidden' }
    $env:SMOKE_LAUNCH_EPOCH_MS = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString()
    $resourceClock = [Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process @start
    $resources.samples += Get-SmokeResourceSample 'before'
    node "$PSScriptRoot/smoke-http.mjs"
    if ($LASTEXITCODE -ne 0) { throw 'Published artifact HTTP smoke failed' }
    if ($Browser) {
        Push-Location "$PSScriptRoot/../tests/smoke"
        try { npm test; if ($LASTEXITCODE -ne 0) { throw 'Published artifact browser smoke failed' } }
        finally { Pop-Location }
    }
} finally {
    if ($process) {
        $resources.samples += Get-SmokeResourceSample 'after'
        $resources.elapsedMs = $resourceClock.Elapsed.TotalMilliseconds
        if (!$resources.samples[0].unavailable -and !$resources.samples[1].unavailable -and $resources.elapsedMs -gt 0) {
            $resources.cpuPercentOneCoreEquivalent = ($resources.samples[1].cpuSeconds - $resources.samples[0].cpuSeconds) * 100000 / $resources.elapsedMs
        }
    }
    $resources | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$evidence/resources.json" -Encoding utf8NoBOM
    if ($process -and !$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit(5000) | Out-Null }
    # Only upload logs; the isolated data directory contains credentials and stays local.
    foreach ($log in @("$evidence/stdout.log", "$evidence/stderr.log")) {
        if (Test-Path -LiteralPath $log) {
            $text = [IO.File]::ReadAllText($log).Replace($password, '[REDACTED]')
            [IO.File]::WriteAllText($log, $text, [Text.UTF8Encoding]::new($false))
        }
    }
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key]) }
}
