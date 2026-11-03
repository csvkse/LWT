<#
.SYNOPSIS
    一键热更新远程 Linux 开发部署中的 SOCKS5 安全代理微服务插件 (无需重启容器，零停机)
#>
[CmdletBinding()]
param(
    [string]$EnvFile = "env/.envdev"
)

$PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& "$PSScriptRoot/deploy-remote.ps1" -HotUpdatePlugin "socks5-proxy" -EnvFile $EnvFile
