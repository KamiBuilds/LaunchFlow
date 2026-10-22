@echo off
setlocal
cd /d "%~dp0"
title LaunchFlow - Make app installer (one-time build)
echo.
echo This builds LaunchFlow-Setup-v0.7.8-x64.exe in the dist folder.
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
echo Done. Use dist\LaunchFlow-Setup-v0.7.8-x64.exe from now on.
pause
exit /b 0
