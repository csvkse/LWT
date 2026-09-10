param(
    [string]$ImageTag = 'linuxwebtool:aot-verify',
    [int]$Port = 15270
)

$ErrorActionPreference = 'Stop'
$container = 'linuxwebtool-aot-smoke'
$base = "http://127.0.0.1:$Port"
$id = '00000000-0000-0000-0000-000000000001'

docker remove --force $container 2>$null | Out-Null
try {
    docker run -d --name $container --publish "$Port`:5270" $ImageTag | Out-Null
    Start-Sleep -Seconds 3

    $password = ((docker logs $container 2>&1 | Select-String '密码 ([A-Za-z0-9]+)' | Select-Object -Last 1).Matches.Groups[1].Value)
    if ([string]::IsNullOrWhiteSpace($password)) { throw '未能从容器日志读取初始化管理员密码' }

    $login = Invoke-RestMethod -Uri "$base/api/Auth/Login" -Method Post -ContentType 'application/json' -Body (@{ username = 'admin'; password = $password } | ConvertTo-Json)
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
            $response = Invoke-WebRequest @request
            $status = [int]$response.StatusCode
        } catch {
            $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode.value__ } else { 0 }
        }
        Write-Host ("{0,-6} {1,3} {2}" -f $case.Method, $status, $case.Uri)
        if ($status -ge 500 -or $status -eq 0) { $failures.Add("$($case.Method) $($case.Uri) => $status") }
    }

    $logs = docker logs $container 2>&1 | Out-String
    foreach ($pattern in @('Dynamic code generation is not supported', 'JsonTypeInfo metadata', 'Reflection.Emit')) {
        if ($logs -match [regex]::Escape($pattern)) { $failures.Add("容器日志包含 AOT 错误：$pattern") }
    }
    if ($failures.Count -gt 0) { throw "接口冒烟失败（$($failures.Count)）：`n$($failures -join "`n")" }
    Write-Host "`n✅ AOT 接口冒烟通过：$($cases.Count) 个路由" -ForegroundColor Green
}
finally {
    docker remove --force $container 2>$null | Out-Null
}
