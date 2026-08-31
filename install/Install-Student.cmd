@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"\"%~dp0Setup-Student.ps1\"\"' -Wait"
if errorlevel 1 exit /b 1
echo.
echo Done. Press any key to close.
pause >nul
