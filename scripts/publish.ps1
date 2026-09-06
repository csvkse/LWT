# 发布 LinuxWebTool 到 Linux（默认 linux-x64 自包含，目标机无需安装 .NET 运行时）
# 用法：./scripts/publish.ps1 [-Runtime linux-x64] [-OutputDir publish/linuxwebtool]
param(
    [string]$Runtime = 'linux-x64',
    [string]$OutputDir = 'publish/linuxwebtool'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Write-Host "== dotnet publish ($Runtime, self-contained) ==" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'src/LinuxWebTool.WebHost/LinuxWebTool.WebHost.csproj') `
    -c Release -r $Runtime --self-contained true `
    -o (Join-Path $root $OutputDir) --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish 失败' }

Write-Host "`n✅ 发布完成：$root/$OutputDir" -ForegroundColor Green
Write-Host @'

Linux 部署步骤（systemd 方式）：
  1. 上传整个 publish/linuxwebtool 目录到目标机 /opt/linuxwebtool
  2. 复制服务单元： sudo cp src/LinuxWebTool.WebHost/Deploy/linuxwebtool.service /etc/systemd/system/
  3. 启动：        sudo systemctl daemon-reload && sudo systemctl enable --now linuxwebtool
  4. 查看日志：    journalctl -u linuxwebtool -f   或  tail -f /opt/linuxwebtool/logs/app-*.txt
  5. 首次启动口令：见 /opt/linuxwebtool/data/admin.json（或在 appsettings.json 预置 Admin:Password）

Docker 方式（在仓库根目录）：
  docker build -t linuxwebtool .
  docker run -d -p 8080:8080 -v linuxwebtool-data:/app/data linuxwebtool
'@
