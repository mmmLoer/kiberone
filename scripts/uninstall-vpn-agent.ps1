#Requires -RunAsAdministrator
param(
    [string] $ServiceName = "KiberoneVpnAgent",
    [string] $InstallDir = "C:\Program Files\KIBERone\VpnAgent",
    [string] $DataDir = "C:\ProgramData\KIBERone\VpnAgent",
    [int] $Port = 9777,
    [switch] $RemoveData
)

$ErrorActionPreference = "Stop"

# Stop tunnel service if registered (name from peer.conf basename)
$peer = Join-Path $DataDir "peer.conf"
if (Test-Path $peer) {
    $tunnelName = [System.IO.Path]::GetFileNameWithoutExtension($peer)
    $tunnelService = "WireGuardTunnel`$$tunnelName"
    & sc.exe stop $tunnelService 2>$null | Out-Null
    Start-Sleep -Seconds 1
    & sc.exe delete $tunnelService 2>$null | Out-Null
}

& sc.exe stop $ServiceName 2>$null | Out-Null
Start-Sleep -Seconds 1
& sc.exe delete $ServiceName 2>$null | Out-Null

Get-NetFirewallRule -DisplayName "KIBERone VPN Agent $Port" -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if (Test-Path $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
if ($RemoveData -and (Test-Path $DataDir)) { Remove-Item -LiteralPath $DataDir -Recurse -Force }

Write-Host "Uninstalled $ServiceName."
