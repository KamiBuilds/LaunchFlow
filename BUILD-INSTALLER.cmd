@echo off
setlocal
cd /d "%~dp0"
title LaunchFlow - Build Windows Installer
echo.
echo This builds the versioned LaunchFlow setup .exe in the dist folder.
echo After it finishes, install THAT setup .exe — not this builder.
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD-INSTALLER.ps1" %*
rem Build tools are for developers only; optional -LaunchSetup opens the completed setup.
set ERR=%ERRORLEVEL%
if %ERR% neq 0 (
  echo.
  echo Build failed with exit code %ERR%.
  pause
  exit /b %ERR%
)
echo.
echo Done. Your versioned Setup EXE is in the dist folder.
pause
exit /b 0
