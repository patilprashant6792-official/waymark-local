@echo off
rem Removes Waymark. Your data stays unless you run: uninstall.cmd -RemoveData
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\waymark.ps1" uninstall %*
echo.
pause
