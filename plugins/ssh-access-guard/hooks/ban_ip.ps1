$ip = if ($args[0]) { $args[0] } elseif ($env:LWT_PARAM) { $env:LWT_PARAM } else { $env:LWT_PARAM_IP }
if (-not $ip) {
    Write-Output '{"protocolVersion":1,"status":"failed","error":"缺少目标 IP 参数","message":"缺少目标 IP 参数"}'
    exit 1
}
if ($ip -eq "127.0.0.1" -or $ip -eq "::1" -or $ip -eq "localhost") {
    Write-Output '{"protocolVersion":1,"status":"failed","error":"安全保护：禁止封禁本地回环地址","message":"安全保护：禁止封禁本地回环地址"}'
    exit 1
}
Write-Output ('{"protocolVersion":1,"status":"succeeded","data":{"ip":"' + $ip + '","banned":true},"message":"已成功将 ' + $ip + ' 加入防护黑名单 (Windows 模拟态)"}')
