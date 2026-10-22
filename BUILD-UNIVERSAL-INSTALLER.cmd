@echo off
setlocal
cd /d "%~dp0"
title LaunchFlow - build universal installer once
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD-UNIVERSAL-INSTALLER.ps1" %*
if errorlevel 1 (
  echo.
  echo Universal installer build failed. Read the error above.
  pause
  exit /b 1
)
echo.
echo The universal installer is at: dist\LaunchFlow-Universal-Installer.exe
echo Run it to download the newest published version. You do NOT need to build again for updates.
pause
