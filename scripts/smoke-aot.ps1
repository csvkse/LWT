param(
    [string]$ImageTag = 'linuxwebtool:aot-verify',
    [int]$Port = 15270,
    [switch]$Browser,
    [switch]$Terminal,
    [string]$Artifacts = 'artifacts/smoke'
)

$ErrorActionPreference = 'Stop'
$container = 'linuxwebtool-aot-smoke-' + [guid]::NewGuid().ToString('N')
$base = "http://127.0.0.1:$Port"
$id = '00000000-0000-0000-0000-000000000001'

$password = [guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $Artifacts -Force | Out-Null
$savedEnv = @{}
foreach ($key in @('SMOKE_URL', 'SMOKE_USERNAME', 'SMOKE_PASSWORD', 'SMOKE_FILE_DIRECTORY', 'TERMINAL_TEST_URL', 'TERMINAL_TEST_USERNAME', 'TERMINAL_TEST_PASSWORD', 'SMOKE_PERF_OUTPUT', 'SMOKE_TARGET', 'SMOKE_TARGET_MODE', 'SMOKE_LAUNCH_EPOCH_MS')) {
    $savedEnv[$key] = [Environment]::GetEnvironmentVariable($key)
}
$resources = @{ schemaVersion = 1; source = 'docker-stats'; sampling = 'before/after smoke; not peak'; samples = @() }
function Get-ContainerResourceSample($phase) {
    $stats = docker stats --no-stream --format '{{json .}}' $container 2>$null
    if ($LASTEXITCODE -ne 0) { return @{ phase = $phase; unavailable = $true } }
    $values = $stats | ConvertFrom-Json
    return @{ phase = $phase; timestamp = [DateTimeOffset]::UtcNow.ToString('o'); cpuPercent = $values.CPUPerc; memoryUsage = $values.MemUsage; memoryPercent = $values.MemPerc; pids = $values.PIDs }
}
try {
    $env:SMOKE_LAUNCH_EPOCH_MS = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString()
    # The idle-reclamation assertion waits 10 seconds; production defaults to 120.
    # Shorten only the disposable fixture's grace, preserving the real cleanup logic.
    docker run -d --name $container --publish "127.0.0.1:$Port`:5270" -e Admin__UserName=admin -e "Admin__Password=$password" -e Terminal__UnusedGraceSeconds=2 $ImageTag | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Docker startup failed' }
    $resources.samples += Get-ContainerResourceSample 'before'
    $env:SMOKE_URL = $base
    $env:SMOKE_USERNAME = 'admin'
    $env:SMOKE_PASSWORD = $password
    $env:SMOKE_FILE_DIRECTORY = '/tmp'
    $env:SMOKE_PERF_OUTPUT = [IO.Path]::GetFullPath((Join-Path $Artifacts 'performance.json'))
    if (!$env:SMOKE_TARGET) { $env:SMOKE_TARGET = 'container-aot' }
    $env:SMOKE_TARGET_MODE = 'native-aot'
    node "$PSScriptRoot/smoke-http.mjs"
    if ($LASTEXITCODE -ne 0) { throw 'HTTP behavioral smoke failed' }

    $login = Invoke-RestMethod -Uri "$base/api/Auth/Login" -TimeoutSec 10 -Method Post -ContentType 'application/json' -Body (@{ username = 'admin'; password = $password } | ConvertTo-Json)
    $token = $login.token
    if ([string]::IsNullOrWhiteSpace($token)) { throw '登录响应未返回 token' }
    $headers = @{ Authorization = "Bearer $token" }

    $cases = @(
        @{ Method = 'GET'; Uri = '/api/Auth/Check' },
        @{ Method = 'GET'; Uri = '/api/Commands' }, @{ Method = 'POST'; Uri = '/api/Commands'; Body = '{}' },
        @{ Method = 'PUT'; Uri = "/api/Commands/$id"; Body = '{}' }, @{ Method = 'DELETE'; Uri = "/api/Commands/$id" },
        @{ Method = 'POST'; Uri = "/api/Commands/$id/Execute"; Body = '{}' }, @{ Method = 'POST'; Uri = '/api/Commands/QuickExecute'; Body = '{"commandText":"printf aot-smoke"}' },
        @{ Method = 'GET'; Uri = "/api/Commands/$id/History?page=1&pageSize=20" },
        @{ Method = 'GET'; Uri = '/api/Files?path=/' }, @{ Method = 'GET'; Uri = '/api/Files/Content?path=/etc/hosts' },
        @{ Method = 'POST'; Uri = '/api/Files/Content'; Body = '{}' }, @{ Method = 'POST'; Uri = '/api/Files/Mkdir'; Body = '{}' },
        @{ Method = 'POST'; Uri = '/api/Files/Rename'; Body = '{}' }, @{ Method = 'DELETE'; Uri = '/api/Files?path=/tmp/aot-no-such' },
        @{ Method = 'GET'; Uri = '/api/Groups' }, @{ Method = 'POST'; Uri = '/api/Groups'; Body = '{}' },
        @{ Method = 'PUT'; Uri = "/api/Groups/$id"; Body = '{}' }, @{ Method = 'DELETE'; Uri = "/api/Groups/$id" },
        @{ Method = 'GET'; Uri = '/api/History?page=1&pageSize=20' }, @{ Method = 'DELETE'; Uri = '/api/History?olderThanDays=9999' },
        @{ Method = 'GET'; Uri = '/api/Logs/Operations?page=1&pageSize=20' }, @{ Method = 'GET'; Uri = '/api/Logs/Files' }, @{ Method = 'GET'; Uri = '/api/Logs/Files/no-such.log' },
        @{ Method = 'GET'; Uri = '/api/Overview' }, @{ Method = 'GET'; Uri = '/api/Schedules' }, @{ Method = 'POST'; Uri = '/api/Schedules'; Body = '{}' },
        @{ Method = 'PUT'; Uri = "/api/Schedules/$id"; Body = '{}' }, @{ Method = 'DELETE'; Uri = "/api/Schedules/$id" },
        @{ Method = 'POST'; Uri = "/api/Schedules/$id/Toggle" }, @{ Method = 'POST'; Uri = "/api/Schedules/$id/RunNow" },
        @{ Method = 'GET'; Uri = "/api/Schedules/$id/Records?page=1&pageSize=20" },
        @{ Method = 'GET'; Uri = '/api/SmbMounts/Support' }, @{ Method = 'GET'; Uri = '/api/SmbMounts' }, @{ Method = 'POST'; Uri = '/api/SmbMounts'; Body = '{}' },
        @{ Method = 'PUT'; Uri = "/api/SmbMounts/$id"; Body = '{}' }, @{ Method = 'DELETE'; Uri = "/api/SmbMounts/$id" }, @{ Method = 'POST'; Uri = "/api/SmbMounts/$id/Mount" }, @{ Method = 'POST'; Uri = "/api/SmbMounts/$id/Unmount"; Body = '{}' },
        @{ Method = 'GET'; Uri = '/api/SystemStatus' }, @{ Method = 'GET'; Uri = '/api/SystemStatus/History?hours=1' }, @{ Method = 'GET'; Uri = '/api/SystemStatus/DiskHistory?hours=1' },
        @{ Method = 'GET'; Uri = '/api/SystemStatus/NetHistory?hours=1' }, @{ Method = 'GET'; Uri = '/api/SystemStatus/ProcessHistory?hours=1' }, @{ Method = 'GET'; Uri = '/api/SystemStatus/ResourceHistory?hours=1' },
        @{ Method = 'GET'; Uri = '/api/Transcode/Jobs?page=1&pageSize=20' }, @{ Method = 'POST'; Uri = '/api/Transcode/Submit'; Body = '{}' },
        @{ Method = 'POST'; Uri = "/api/Transcode/Jobs/$id/Cancel" }, @{ Method = 'POST'; Uri = "/api/Transcode/Jobs/Retry/$id" }, @{ Method = 'POST'; Uri = '/api/Transcode/Jobs/ClearFinished' },
        @{ Method = 'GET'; Uri = '/api/Transcode/Presets' }, @{ Method = 'POST'; Uri = '/api/Transcode/Presets'; Body = '{}' }, @{ Method = 'PUT'; Uri = "/api/Transcode/Presets/$id"; Body = '{}' },
        @{ Method = 'DELETE'; Uri = "/api/Transcode/Presets/$id" }, @{ Method = 'GET'; Uri = '/api/Transcode/Presets/Export' }, @{ Method = 'POST'; Uri = '/api/Transcode/Presets/Import'; Body = '[]' },
        @{ Method = 'GET'; Uri = '/api/Transcode/WatchRules' }, @{ Method = 'POST'; Uri = '/api/Transcode/WatchRules'; Body = '{}' }, @{ Method = 'PUT'; Uri = "/api/Transcode/WatchRules/$id"; Body = '{}' },
        @{ Method = 'DELETE'; Uri = "/api/Transcode/WatchRules/$id" }, @{ Method = 'POST'; Uri = "/api/Transcode/WatchRules/$id/Toggle" }, @{ Method = 'GET'; Uri = '/api/Transcode/DetectFfmpeg' }
    )

    $failures = [System.Collections.Generic.List[string]]::new()
    foreach ($case in $cases) {
        try {
            $request = @{ Uri = $base + $case.Uri; Method = $case.Method; Headers = $headers; TimeoutSec = 20 }
            if ($case.ContainsKey('Body')) { $request.ContentType = 'application/json'; $request.Body = $case.Body }
            $response = Invoke-WebRequest @request -SkipHttpErrorCheck
            $status = [int]$response.StatusCode
        } catch {
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode.value__ } else { 0 }
        }
        Write-Host ("{0,-6} {1,3} {2}" -f $case.Method, $status, $case.Uri)
        # Read routes must succeed; the intentionally missing log file must be 404.
        # Empty requests / missing IDs are negative contracts, never 401 or 405.
        $expected = if ($case.Uri -eq '/api/Logs/Files/no-such.log') { @(404) }
            elseif ($case.Method -eq 'GET' -or $case.Uri -in @('/api/Commands/QuickExecute', '/api/History?olderThanDays=9999', '/api/Transcode/Jobs/ClearFinished', '/api/Transcode/Presets/Import')) { @(200) }
            elseif ($case.Uri.Contains($id) -and $case.ContainsKey('Body')) { @(400, 404) }
            elseif ($case.Uri.Contains($id) -or $case.Uri -eq '/api/Files?path=/tmp/aot-no-such') { @(404) }
            else { @(400) }
        if ($status -notin $expected) { $failures.Add("$($case.Method) $($case.Uri) => $status; expected $expected") }
    }

    $logs = docker logs $container 2>&1 | Out-String
    foreach ($pattern in @('Dynamic code generation is not supported', 'JsonTypeInfo metadata', 'Reflection.Emit')) {
        if ($logs -match [regex]::Escape($pattern)) { $failures.Add("容器日志包含 AOT 错误：$pattern") }
    }
    if ($failures.Count -gt 0) { throw "接口冒烟失败（$($failures.Count)）：`n$($failures -join "`n")" }
    if ($Terminal) {
        $env:TERMINAL_TEST_URL = $base
        $env:TERMINAL_TEST_USERNAME = 'admin'
        $env:TERMINAL_TEST_PASSWORD = $password
        node "$PSScriptRoot/verify-terminal.mjs"
        if ($LASTEXITCODE -ne 0) { throw 'Terminal smoke failed' }
    }
    if ($Browser) {
        Push-Location "$PSScriptRoot/../tests/smoke"
        try {
            npm test
            if ($LASTEXITCODE -ne 0) { throw 'Browser smoke failed' }
        } finally { Pop-Location }
    }
    Write-Host "`n✅ AOT 接口冒烟通过：$($cases.Count) 个路由" -ForegroundColor Green
}
finally {
    $resources.samples += Get-ContainerResourceSample 'after'
    $resources | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath "$Artifacts/resources.json" -Encoding utf8NoBOM
    $diagnostic = docker logs $container 2>&1 | Out-String
    $diagnostic.Replace($password, '[REDACTED]') | Set-Content -LiteralPath "$Artifacts/container.log" -Encoding utf8NoBOM
    docker rm --force $container 2>$null | Out-Null
    foreach ($key in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnv[$key]) }
}
