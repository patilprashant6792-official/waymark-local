@echo off
rem Builds and installs Waymark for the current user, starts it and opens it. Double-click it or run it from a terminal.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\waymark.ps1" install %*
echo.
pause
