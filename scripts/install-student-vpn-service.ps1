#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time install of KIBERone Student VPN service (no UAC prompts during lessons).

.EXAMPLE
  .\install-student-vpn-service.ps1 -SourceDir ".\dist\Student-win-x64"
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceDir,

    [string] $InstallDir = "",
    [switch] $InPlace,
    [switch] $AllowMissingWireGuard,
    [string] $VpnDir = "$env:ProgramData\KIBERone\Student\vpn",
    [string] $ServiceName = "KIBERoneStudentVpn"
)

$ErrorActionPreference = "Stop"

function Assert-File([string] $Path, [string] $Hint) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing required file: $Path. $Hint"
    }
}

function New-SidAccessRule(
    [string] $Sid,
    [System.Security.AccessControl.FileSystemRights] $Rights
) {
    $identity = New-Object System.Security.Principal.SecurityIdentifier($Sid)
    # ContainerInherit (1) + ObjectInherit (2) — avoid -bor inside New-Object args (PS 5.x quirk)
    $inherit = [System.Security.AccessControl.InheritanceFlags]3
    return New-Object System.Security.AccessControl.FileSystemAccessRule(
        $identity,
        $Rights,
        $inherit,
        [System.Security.AccessControl.PropagationFlags]::None,
        [System.Security.AccessControl.AccessControlType]::Allow)
}

function Set-VpnDirectoryAcl([string] $Path) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule((New-SidAccessRule "S-1-5-18" ([System.Security.AccessControl.FileSystemRights]::FullControl)))
    $acl.AddAccessRule((New-SidAccessRule "S-1-5-32-544" ([System.Security.AccessControl.FileSystemRights]::FullControl)))
    $acl.AddAccessRule((New-SidAccessRule "S-1-5-32-545" ([System.Security.AccessControl.FileSystemRights]::Modify)))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Get-ServiceBinaryPath([string] $ExecutablePath) {
    return "`"$ExecutablePath`" /vpn-bridge"
}

function Set-ServiceBinaryPath([string] $Name, [string] $BinaryPath) {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$Name'" -ErrorAction Stop
    $result = $service | Invoke-CimMethod -MethodName Change -Arguments @{ PathName = $BinaryPath }
    if ($result.ReturnValue -ne 0) {
        throw "Win32_Service.Change failed with code $($result.ReturnValue) for path: $BinaryPath"
    }
}

function Ensure-WireGuardPrerequisite {
    $wireguardExe = "${env:ProgramFiles}\WireGuard\wireguard.exe"
    if (Test-Path -LiteralPath $wireguardExe) {
        Write-Host "WireGuard prerequisite: OK ($wireguardExe)"
        return
    }

    Write-Host "WireGuard NT not detected. Installing WireGuard 1.1 (one-time kernel driver) ..."
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        if ($AllowMissingWireGuard) {
            Write-Warning "winget missing; WireGuard not installed (-AllowMissingWireGuard)."
            return
        }
        throw "WireGuard not found and winget is missing. Install from https://www.wireguard.com/install/ then rerun."
    }

    & winget install WireGuard.WireGuard --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0 -and -not (Test-Path -LiteralPath $wireguardExe)) {
        if ($AllowMissingWireGuard) {
            Write-Warning "winget install WireGuard failed (-AllowMissingWireGuard)."
            return
        }
        throw "winget install WireGuard failed. Install manually from https://www.wireguard.com/install/"
    }

    if (-not (Test-Path -LiteralPath $wireguardExe) -and -not $AllowMissingWireGuard) {
        throw "WireGuard still missing after install attempt: $wireguardExe"
    }
}

$sourceExe = Join-Path $SourceDir "Kiberone.Student.exe"
Assert-File $sourceExe "Publish Student first."
Assert-File (Join-Path $SourceDir "native\tunnel.dll") "Copy tunnel.dll into native\."
Assert-File (Join-Path $SourceDir "native\wireguard.dll") "Copy wireguard.dll into native\."

Ensure-WireGuardPrerequisite

$resolvedSource = (Resolve-Path $SourceDir).Path
if ($InPlace) {
    $InstallDir = $resolvedSource
    Write-Host "In-place mode: service will use $InstallDir"
} elseif ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = "C:\Program Files\KIBERone\Student"
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $VpnDir | Out-Null

if ($InstallDir -ne $resolvedSource) {
    Write-Host "Copying Student to $InstallDir ..."
    Copy-Item -Path (Join-Path $resolvedSource "*") -Destination $InstallDir -Recurse -Force
} else {
    Write-Host "Using existing Student build in $InstallDir (no copy)."
}

Write-Host "Configuring VPN directory ACL: $VpnDir"
Set-VpnDirectoryAcl $VpnDir
Write-Host "Configuring Student install ACL for in-app updates: $InstallDir"
Set-VpnDirectoryAcl $InstallDir

$installedExe = (Resolve-Path -LiteralPath (Join-Path $InstallDir "Kiberone.Student.exe")).Path
$binPath = Get-ServiceBinaryPath $installedExe

Write-Host "Service binary path: $binPath"

Write-Host "Registering Windows service $ServiceName ..."
& sc.exe stop $ServiceName 2>$null | Out-Null
Start-Sleep -Seconds 1
& sc.exe delete $ServiceName 2>$null | Out-Null
Start-Sleep -Seconds 1

$createResult = & sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "KIBERone Student VPN" 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe create failed: $createResult"
}

& sc.exe description $ServiceName "WireGuard tunnel bridge for KIBERone Student. Installed once; no UAC during lessons." | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# Let standard users start/query the already-installed bridge without a second UAC prompt.
# SY/BA keep full control; BU gets start/stop/query.
$sdResult = & sc.exe sdset $ServiceName "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;IU)(A;;RPWPDTRC;;;BU)" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Could not loosen service ACL (non-fatal): $sdResult"
}

$startResult = & sc.exe start $ServiceName 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe start failed: $startResult"
}
Start-Sleep -Seconds 3

$status = (& sc.exe query $ServiceName | Out-String)
if ($status -notmatch "RUNNING") {
    throw "Service $ServiceName did not start. Check Event Viewer and $installedExe /vpn-bridge"
}

Write-Host ""
Write-Host "Installed successfully."
Write-Host "  Student:  $installedExe"
Write-Host "  VPN dir:  $VpnDir"
Write-Host "  Service:  $ServiceName (running)"
Write-Host ""
Write-Host "You can keep launching Student from dist\Student-win-x64."
Write-Host "Tutor can push .conf files and enable VPN without UAC prompts."

Write-Host ""
Write-Host "Verifying VPN probes (optional; failure must not undo service install) ..."
& $installedExe /verify-vpn
if ($LASTEXITCODE -ne 0) {
    Write-Warning "VPN probe check failed. Service $ServiceName is still installed and running."
    Write-Warning "Real classroom configs from Tutor should still connect. Re-check WireGuard if needed."
    # Exit 0: callers (Inno / Repair / old in-app UAC path) must treat service install as success.
    exit 0
}
