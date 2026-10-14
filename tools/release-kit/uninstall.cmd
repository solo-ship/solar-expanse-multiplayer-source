@echo off
rem Removes Solar Expanse Multiplayer and puts back what the install replaced (double-click this).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
echo.
pause
