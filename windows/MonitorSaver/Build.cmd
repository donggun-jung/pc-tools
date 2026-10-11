@echo off
setlocal
cd /d "%~dp0"

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [!] csc.exe not found ^(.NET Framework 4.x required^)
  if not defined CI pause
  exit /b 1
)

rem csc reads a BOM-less source using the system ANSI codepage, which mangles Korean.
rem Normalize MonitorSaver.cs to UTF-8 with BOM before compiling.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p = Join-Path $PWD 'MonitorSaver.cs'; $t = Get-Content -Raw -Encoding UTF8 $p; [IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($true)))"

echo [*] compiling...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 /out:MonitorSaver.exe MonitorSaver.cs /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll

if errorlevel 1 (
  echo.
  echo [!] BUILD FAILED
  if not defined CI pause
  exit /b 1
)

echo [*] Build OK: %~dp0MonitorSaver.exe
endlocal
