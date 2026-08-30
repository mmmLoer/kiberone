#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Full one-time Student VPN setup: Windows service + optional peer.conf.

.EXAMPLE
  .\install-student-vpn.ps1 -SourceDir ".\dist\Student-win-x64"
  .\install-student-vpn.ps1 -SourceDir ".\dist\Student-win-x64" -InPlace
  .\install-student-vpn.ps1 -SourceDir ".\dist\Student-win-x64" -PeerConf ".\pc05.conf"
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceDir,

    [switch] $InPlace,
    [string] $PeerConf = "",
    [string] $TargetDir = "$env:ProgramData\KIBERone\Student\vpn"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$params = @{
    SourceDir = $SourceDir
}
if ($InPlace) { $params.InPlace = $true }
& (Join-Path $scriptDir "install-student-vpn-service.ps1") @params

if ([string]::IsNullOrWhiteSpace($PeerConf)) {
    Write-Host "Peer.conf not provided. Tutor can distribute configs automatically."
    return
}

if (-not (Test-Path -LiteralPath $PeerConf)) { throw "peer.conf not found: $PeerConf" }

New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
$dest = Join-Path $TargetDir "peer.conf"
Copy-Item -LiteralPath $PeerConf -Destination $dest -Force
Write-Host "Installed VPN profile: $dest"
