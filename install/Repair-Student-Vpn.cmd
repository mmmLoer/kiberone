@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$installDir = Join-Path $env:ProgramFiles 'KIBERone\Student';" ^
  "$candidates = @(" ^
  "  (Join-Path $installDir 'service\install-student-vpn-service.ps1')," ^
  "  (Join-Path $PSScriptRoot 'service\install-student-vpn-service.ps1')," ^
  "  (Join-Path (Split-Path $PSScriptRoot -Parent) 'scripts\install-student-vpn-service.ps1')" ^
  ");" ^
  "$script = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1;" ^
  "if (-not $script) { Write-Error 'VPN install script not found.'; exit 1 };" ^
  "if (-not (Test-Path -LiteralPath (Join-Path $installDir 'Kiberone.Student.exe'))) { Write-Error ('Student not installed: ' + $installDir); exit 1 };" ^
  "$argList = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $script, '-SourceDir', $installDir, '-InPlace');" ^
  "$p = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru;" ^
  "exit $p.ExitCode"
if errorlevel 1 (
  echo VPN repair failed.
  pause
  exit /b 1
)
echo VPN service installed.
pause
