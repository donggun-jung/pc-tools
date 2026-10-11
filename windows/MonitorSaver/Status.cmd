@echo off
rem MonitorSaver diagnostics: is it running, when was it last alive, and how did it die?
cd /d "%~dp0"
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Status.ps1"
pause
