param($ip)
if ([string]::IsNullOrWhiteSpace($ip)) {
    $ip = $env:LWT_PARAM_IP
}
if ([string]::IsNullOrWhiteSpace($ip) -and $input) {
    try {
        $json = $input | Out-String | ConvertFrom-Json
        $ip = $json.parameters.ip
    } catch {}
}
if ([string]::IsNullOrWhiteSpace($ip)) {
    Write-Output '{"protocolVersion":1,"status":"failed","error":"缺少目标 IP 参数","message":"缺少目标 IP 参数"}'
    exit 1
}
if ($ip -eq "127.0.0.1" -or $ip -eq "::1" -or $ip -eq "localhost") {
    Write-Output '{"protocolVersion":1,"status":"failed","error":"安全保护：禁止封禁本地回环地址","message":"安全保护：禁止封禁本地回环地址"}'
    exit 1
}
Write-Output "{`"protocolVersion`":1,`"status`":`"succeeded`",`"data`":{`"ip`":`"$ip`"},`"message`":`"(Windows模拟) 成功封禁 $ip`"}"
