$now = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
Write-Output ('{"protocolVersion":1,"status":"succeeded","data":[{"user":"admin","ip":"192.168.1.100","status":"success","time":"' + $now + '"}],"message":"获取登录审计记录成功 (Windows 模拟态)"}')
