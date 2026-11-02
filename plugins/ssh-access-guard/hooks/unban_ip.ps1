$ip = if ($args[0]) { $args[0] } elseif ($env:LWT_PARAM) { $env:LWT_PARAM } else { $env:LWT_PARAM_IP }
if (-not $ip) {
    Write-Output '{"protocolVersion":1,"status":"failed","error":"缺少目标 IP 参数","message":"缺少目标 IP 参数"}'
    exit 1
}
Write-Output ('{"protocolVersion":1,"status":"succeeded","data":{"ip":"' + $ip + '","unbanned":true},"message":"成功解除 ' + $ip + ' 的安全防护黑名单 (Windows 模拟态)"}')
