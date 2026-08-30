#Requires -RunAsAdministrator
param(
    [string] $ConfigPath = "$env:ProgramData\KIBERone\Student\vpn\peer.conf",
    [string] $SourceDir = "C:\Users\goroh\Downloads\kiberone\dist\Student-win-x64-new"
)

$ErrorActionPreference = "Stop"
& (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "restart-student-vpn-service.ps1") -SourceDir $SourceDir

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "Config not found: $ConfigPath"
}

$original = Get-Content -LiteralPath $ConfigPath -Raw
if ($original -match 'AllowedIPs\s*=\s*0\.0\.0\.0/0') {
    $fixed = $original -replace 'AllowedIPs\s*=\s*0\.0\.0\.0/0', @'
AllowedIPs = 0.0.0.0/5, 8.0.0.0/7, 11.0.0.0/8, 12.0.0.0/6, 16.0.0.0/4, 32.0.0.0/3, 64.0.0.0/2, 128.0.0.0/3, 160.0.0.0/5, 168.0.0.0/6, 172.0.0.0/12, 172.32.0.0/11, 172.64.0.0/10, 172.128.0.0/9, 173.0.0.0/8, 174.0.0.0/7, 176.0.0.0/4, 192.0.0.0/9, 192.128.0.0/11, 192.160.0.0/13, 193.0.0.0/8, 194.0.0.0/7, 196.0.0.0/6, 200.0.0.0/5, 208.0.0.0/4, 224.0.0.0/3
'@
    Set-Content -LiteralPath $ConfigPath -Value $fixed -NoNewline
    Write-Host "Updated AllowedIPs for classroom LAN access."
}

& sc.exe stop "WireGuardTunnel`$peer" 2>$null | Out-Null
Start-Sleep 2
& sc.exe start "WireGuardTunnel`$peer" 2>&1
Start-Sleep 3
& sc.exe query "WireGuardTunnel`$peer"
Write-Host ""
Get-Content -LiteralPath $ConfigPath | Select-String "AllowedIPs"
