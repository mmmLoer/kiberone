@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$desktop = [Environment]::GetFolderPath('Desktop'); $setup = '%~dp0Setup-Student.ps1'; $argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $setup, '-DesktopPath', $desktop); $p = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru; exit $p.ExitCode"
if errorlevel 1 (
  echo.
  echo Installation finished with warnings. Check messages above.
  pause
  exit /b 1
)
echo.
echo Done. Press any key to close.
pause >nul
