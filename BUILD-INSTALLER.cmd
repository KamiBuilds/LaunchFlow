@echo off
setlocal
cd /d "%~dp0"
title LaunchFlow - Build Windows Installer
echo.
echo Builds the latest LaunchFlow Windows Setup into the dist folder.
echo After it finishes, install THAT setup .exe — not this builder.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD-INSTALLER.ps1" -Yes -LaunchSetup %*
rem -Yes auto-installs build tools; -LaunchSetup opens the finished setup .exe locally.
set ERR=%ERRORLEVEL%
if %ERR% neq 0 (
  echo.
  echo Build failed with exit code %ERR%.
  pause
  exit /b %ERR%
)
echo.
echo Done. Your new Setup EXE is in the dist folder.
pause
exit /b 0
