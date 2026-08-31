#Requires -RunAsAdministrator
param(
    [string] $SourceDir = "C:\Users\goroh\Downloads\kiberone\dist\Student-win-x64",
    [string] $ServiceName = "KIBERoneStudentVpn"
)

$ErrorActionPreference = "Stop"

$exe = Join-Path $SourceDir "Kiberone.Student.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Student exe not found: $exe"
}
$exe = (Resolve-Path -LiteralPath $exe).Path
$binPath = "`"$exe`" /vpn-bridge"

Write-Host "Student exe : $exe"
Write-Host "Service path: $binPath"

Write-Host "Stopping processes..."
Get-Process -Name "Kiberone.Student" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
& sc.exe stop $ServiceName 2>$null | Out-Null
Start-Sleep -Seconds 2
& sc.exe delete $ServiceName 2>$null | Out-Null
Start-Sleep -Seconds 2

$createResult = & sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "KIBERone Student VPN" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe create failed: $createResult"
}

& sc.exe description $ServiceName "WireGuard tunnel bridge for KIBERone Student." | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

$startResult = & sc.exe start $ServiceName 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe start failed: $startResult"
}
Start-Sleep -Seconds 3

Write-Host ""
& sc.exe qc $ServiceName
Write-Host ""
$status = (& sc.exe query $ServiceName | Out-String)
Write-Host $status
if ($status -notmatch "RUNNING") {
    throw "Service did not start. Run scripts\view-vpn-log.ps1"
}

Write-Host "Service restarted successfully."
