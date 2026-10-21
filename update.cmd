@echo off
rem Pulls the latest version of the repository and installs it. Your data is not touched.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\waymark.ps1" update %*
echo.
pause
