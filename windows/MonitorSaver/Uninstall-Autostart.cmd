@echo off
rem Remove the logon autostart task. Does not stop the running instance.
cd /d "%~dp0"
chcp 65001 >nul
"%~dp0MonitorSaver.exe" -autostart off
timeout /t 2 >nul
schtasks /Query /TN "MonitorSaver" 2>nul || echo Autostart task removed.
echo.
pause
