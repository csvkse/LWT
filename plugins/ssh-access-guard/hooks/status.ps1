$target = if ($env:LWT_TARGET) { $env:LWT_TARGET } else { "host" }
Write-Output ('{"protocolVersion":1,"status":"succeeded","data":{"status":"Active","target":"' + $target + '","activeCount":1,"bannedCount":0,"message":"SSH 守卫待命 (Windows 模拟态)"},"message":"SSH 守卫待命"}')
