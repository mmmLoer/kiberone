#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One-time install of Kiberone.VpnAgent (Windows Service + WireGuard embeddable tunnel).

.EXAMPLE
  .\install-vpn-agent.ps1 -SourceDir "D:\KIBERone-Classroom\dist\VpnAgent-win-x64" -ApiToken "your-secret" -PeerConf ".\pc05.conf"
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceDir,

    [Parameter(Mandatory = $true)]
    [string] $ApiToken,

    [Parameter(Mandatory = $true)]
    [string] $PeerConf,

    [string] $InstallDir = "C:\Program Files\KIBERone\VpnAgent",
    [string] $DataDir = "C:\ProgramData\KIBERone\VpnAgent",
    [int] $Port = 9777,
    [string] $AllowedRemoteAddresses = "",
    [string] $ServiceName = "KiberoneVpnAgent"
)

$ErrorActionPreference = "Stop"

function Assert-File([string] $Path, [string] $Hint) {
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing required file: $Path. $Hint"
    }
}

$exeName = "Kiberone.VpnAgent.exe"
$sourceExe = Join-Path $SourceDir $exeName
Assert-File $sourceExe "Publish the project first (see scripts\publish-vpn-agent.ps1)."
Assert-File (Join-Path $SourceDir "tunnel.dll") "Build embeddable-dll-service and copy amd64\tunnel.dll into the publish folder."
Assert-File (Join-Path $SourceDir "wireguard.dll") "Download from https://download.wireguard.com/wireguard-nt/"
Assert-File $PeerConf "Provide the per-PC WireGuard .conf for this peer."

if ($ApiToken.Length -lt 16) { throw "ApiToken must be at least 16 characters." }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

Write-Host "Stopping existing service (if any)..."
& sc.exe stop $ServiceName 2>$null | Out-Null
Start-Sleep -Seconds 1
& sc.exe delete $ServiceName 2>$null | Out-Null
Start-Sleep -Seconds 1

Write-Host "Copying files to $InstallDir ..."
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallDir -Recurse -Force

$peerDest = Join-Path $DataDir "peer.conf"
Copy-Item -LiteralPath $PeerConf -Destination $peerDest -Force

$settings = @{
    Logging = @{
        LogLevel = @{
            Default = "Information"
            "Microsoft.Hosting.Lifetime" = "Information"
            "Microsoft.AspNetCore" = "Warning"
        }
    }
    VpnAgent = @{
        Port = $Port
        ApiToken = $ApiToken
        ConfigPath = $peerDest
        AllowedRemoteAddresses = $AllowedRemoteAddresses
    }
} | ConvertTo-Json -Depth 6
Set-Content -LiteralPath (Join-Path $InstallDir "appsettings.json") -Value $settings -Encoding UTF8

# Restrict peer.conf: SYSTEM + Administrators only
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$system = New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$admins = New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.AddAccessRule($system)
$acl.AddAccessRule($admins)
Set-Acl -Path $DataDir -AclObject $acl

$binPath = "`"$(Join-Path $InstallDir $exeName)`""
Write-Host "Creating Windows Service $ServiceName ..."
& sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "KIBERone VPN Agent"
if ($LASTEXITCODE -ne 0) { throw "sc create failed with $LASTEXITCODE" }
& sc.exe description $ServiceName "HTTP control API (:$Port) for WireGuard embeddable tunnel to wg1"
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000

Write-Host "Firewall rule for TCP $Port ..."
$ruleName = "KIBERone VPN Agent $Port"
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Profile Any | Out-Null

Write-Host "Starting service..."
& sc.exe start $ServiceName
Start-Sleep -Seconds 2

try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/health" -TimeoutSec 5
    Write-Host "Health OK:" ($health | ConvertTo-Json -Compress)
} catch {
    Write-Warning "Service started but health check failed: $_"
}

Write-Host ""
Write-Host "Installed. Router examples:"
Write-Host "  curl -H `"X-Vpn-Token: $ApiToken`" http://PC_IP:$Port/v1/status"
Write-Host "  curl -X POST -H `"X-Vpn-Token: $ApiToken`" http://PC_IP:$Port/v1/connect"
Write-Host "  curl -X POST -H `"X-Vpn-Token: $ApiToken`" http://PC_IP:$Port/v1/disconnect"
