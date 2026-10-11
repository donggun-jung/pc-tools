@echo off
rem Register MonitorSaver to start at logon (Task Scheduler, no admin needed).
cd /d "%~dp0"
chcp 65001 >nul
"%~dp0MonitorSaver.exe" -autostart on
timeout /t 2 >nul
schtasks /Query /TN "MonitorSaver"
echo.
pause
