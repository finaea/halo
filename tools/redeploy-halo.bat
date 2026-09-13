@echo off
REM Double-click this to run the full Halo dev cycle:
REM   stop widgets -> stop collector task -> build into dist\app -> restart both.
REM
REM Deliberately does NOT self-elevate. Stopping and starting your own \Halo\ tasks needs no
REM admin, and running the whole script elevated would start Halo.Widgets elevated too -
REM which is not how it is supposed to run (medium integrity, like any user app).
title Halo redeploy

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0redeploy-halo.ps1"
echo.
echo ---------------------------------------------
echo Finished. Review the output above, then close.
pause
