@echo off
setlocal
title LinuxWebTool Host

set "lwt_opened=0"

:run
set "lwt_exit_code=0"
pushd "%~dp0" || (
    set "lwt_exit_code=1"
    goto prompt
)

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [LinuxWebTool] dotnet SDK was not found in PATH. Please install .NET 10 SDK: https://dotnet.microsoft.com/download
    set "lwt_exit_code=1"
    popd
    goto prompt
)

echo =====================================================================
echo   LinuxWebTool Host is starting...
echo   URL: http://localhost:5270/app/
echo   Data directory: src\LinuxWebTool.WebHost\data
echo   To stop: Press Ctrl+C in this window
echo =====================================================================
echo.

if not "%LWT_NO_BROWSER%"=="1" if "%lwt_opened%"=="0" (
    set "lwt_opened=1"
    start "" cmd /c "timeout /t 3 >nul & start http://localhost:5270/app/"
)

dotnet run --project "src\LinuxWebTool.WebHost\LinuxWebTool.WebHost.csproj" --urls "http://localhost:5270"
set "lwt_exit_code=%ERRORLEVEL%"

popd

:prompt
echo.
choice /C RQ /N /M "[LinuxWebTool] Press R to start again, Q to close: "
if errorlevel 2 exit /b %lwt_exit_code%
goto run
