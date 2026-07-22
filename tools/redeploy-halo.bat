@echo off
REM Double-click this to run the full Halo dev cycle:
REM   stop widgets -> stop collector -> publish -> restart both.
REM Self-elevates (UAC prompt) so it can stop the elevated collector and control the task.
title Halo redeploy

REM --- self-elevate: relaunch via UAC if not already running as administrator ---
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Requesting administrator rights...
    powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

REM --- elevated from here ---
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0redeploy-halo.ps1"
echo.
echo ---------------------------------------------
echo Finished. Review the output above, then close.
pause
