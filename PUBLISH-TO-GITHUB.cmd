@echo off
setlocal
cd /d "%~dp0"
where git >nul 2>nul
if errorlevel 1 (
  echo Git for Windows is required: https://git-scm.com/download/win
  pause
  exit /b 1
)
echo This uploads LaunchFlow source to the PUBLIC repository KamiBuilds/LaunchFlow.
echo Do not run this inside a folder containing real credentials or browser profiles.
set /p CONFIRM="Proceed and upload LaunchFlow source? [Y/N] "
if /I not "%CONFIRM%"=="Y" exit /b 0
if not exist ".git" git init -b main
if errorlevel 1 goto failed
git remote remove origin >nul 2>nul
git remote add origin https://github.com/KamiBuilds/LaunchFlow.git
git add .
git commit -m "Add LaunchFlow v0.7.8 source and universal installer"
if errorlevel 1 goto failed
git fetch origin main
if errorlevel 1 goto failed
git merge origin/main --allow-unrelated-histories -X ours -m "Merge existing GitHub repository setup"
if errorlevel 1 goto failed
git push -u origin main
if errorlevel 1 goto failed
echo SUCCESS. GitHub project source uploaded.
pause
exit /b 0
:failed
echo Upload was not completed. Check the Git error above.
pause
exit /b 1
