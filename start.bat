@echo off
title LinuxWebTool - Linux 指令控制台
cd /d "%~dp0"

rem ============ 环境检查 ============
where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 dotnet，请先安装 .NET 10 SDK: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo ============================================
echo   LinuxWebTool 快速启动
echo   地址: http://localhost:5270/app/
echo   首次口令: src\LinuxWebTool.WebHost\data\admin.json
echo   停止服务: 在本窗口按 Ctrl+C
echo ============================================
echo.

rem 延迟 6 秒后自动打开浏览器（设置 LWT_NO_BROWSER=1 可跳过）
if not "%LWT_NO_BROWSER%"=="1" (
    start "" cmd /c "timeout /t 6 >nul & start http://localhost:5270/app/"
)

rem dotnet run 自带增量编译；首次启动会自动建库并生成管理员口令
dotnet run --project src\LinuxWebTool.WebHost --urls http://localhost:5270

echo.
echo [服务已停止]
pause
