@echo off
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$setup = '%~dp0Setup-Student.ps1'; $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $setup); $p = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru; exit $p.ExitCode"
set "INSTALL_EXIT=%ERRORLEVEL%"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Create-Student-Shortcut.ps1"
if errorlevel 1 (
  echo Failed to create desktop shortcut.
  pause
  exit /b 1
)

if not "%INSTALL_EXIT%"=="0" (
  echo.
  echo Installation finished with warnings. Check messages above.
  pause
  exit /b %INSTALL_EXIT%
)

echo.
echo Done. Press any key to close.
pause >nul
