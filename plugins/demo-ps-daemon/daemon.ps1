$counter = 0
while ($true) {
    $counter++
    $time = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Output "[$time] [PS-Daemon] Heartbeat tick #$counter - Plugin: $env:LWT_PLUGIN_ID"
    Start-Sleep -Seconds 30
}
