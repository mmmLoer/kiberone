@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Tutor.ps1"
if errorlevel 1 (
  echo Installation failed.
  pause
  exit /b 1
)
echo.
echo Done. Press any key to close.
pause >nul
