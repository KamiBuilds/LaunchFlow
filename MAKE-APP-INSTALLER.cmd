@echo off
REM Clear name: creates the real Windows app installer (setup .exe).
REM Other PCs only need that setup .exe — never this script.
call "%~dp0BUILD-INSTALLER.cmd" %*
