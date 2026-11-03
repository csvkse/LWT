<#
.SYNOPSIS
    一键热更新远程 Linux 开发部署中的 MCP Bridge 微服务插件 (无需重启容器，零停机)
#>
[CmdletBinding()]
param(
    [string]$EnvFile = "env/.envdev"
)

$PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
& "$PSScriptRoot/deploy-remote.ps1" -HotUpdatePlugin "mcp-bridge" -EnvFile $EnvFile
