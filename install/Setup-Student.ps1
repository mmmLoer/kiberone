#Requires -RunAsAdministrator
param(
    [string] $InstallDir = "$env:ProgramFiles\KIBERone\Student",
    [string] $DesktopPath = ""
)

$ErrorActionPreference = "Stop"
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDir = Join-Path $packageRoot "app"
$serviceScriptSource = Join-Path $packageRoot "service\install-student-vpn-service.ps1"
$serviceScriptInstalled = Join-Path $InstallDir "service\install-student-vpn-service.ps1"

if (-not (Test-Path -LiteralPath (Join-Path $sourceDir "Kiberone.Student.exe"))) {
    throw "Installer package is incomplete: app\Kiberone.Student.exe not found."
}

if (-not (Test-Path -LiteralPath $serviceScriptSource)) {
    throw "Installer package is incomplete: service\install-student-vpn-service.ps1 not found."
}

function Get-DesktopPath([string] $Override) {
    if (-not [string]::IsNullOrWhiteSpace($Override)) {
        return $Override
    }

    $desktop = [Environment]::GetFolderPath("Desktop")
    if ([string]::IsNullOrWhiteSpace($desktop)) {
        $desktop = Join-Path $env:USERPROFILE "Desktop"
    }

    New-Item -ItemType Directory -Force -Path $desktop | Out-Null
    return $desktop
}

function New-DesktopShortcut(
    [string] $Desktop,
    [string] $Name,
    [string] $TargetExe,
    [string] $WorkDir,
    [string] $Description
) {
    $shortcutPath = Join-Path $Desktop "$Name.lnk"
    $wsh = New-Object -ComObject WScript.Shell
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $TargetExe
    $shortcut.WorkingDirectory = $WorkDir
    $shortcut.Description = $Description
    $shortcut.Save()
    return $shortcutPath
}

Write-Host "Installing KIBERone Student to $InstallDir ..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $sourceDir "*") -Destination $InstallDir -Recurse -Force

New-Item -ItemType Directory -Force -Path (Split-Path $serviceScriptInstalled) | Out-Null
Copy-Item -LiteralPath $serviceScriptSource -Destination $serviceScriptInstalled -Force

$vpnError = $null
try {
    & $serviceScriptInstalled -SourceDir $InstallDir -InPlace
} catch {
    $vpnError = $_
    Write-Warning "VPN service setup failed: $($_.Exception.Message)"
}

$installedExe = Join-Path $InstallDir "Kiberone.Student.exe"
if (-not (Test-Path -LiteralPath $installedExe)) {
    throw "Installation incomplete: $installedExe not found."
}

$desktop = Get-DesktopPath $DesktopPath
$shortcutPath = New-DesktopShortcut -Desktop $desktop -Name "KIBERone Student" `
    -TargetExe $installedExe -WorkDir $InstallDir -Description "KIBERone Classroom Student"

Write-Host ""
Write-Host "Installation complete."
Write-Host "  App:      $installedExe"
Write-Host "  Shortcut: $shortcutPath"
Write-Host "  VPN repair: $serviceScriptInstalled"

if ($null -eq $vpnError) {
    Write-Host "  VPN service: KIBERoneStudentVpn (running)"
} else {
    Write-Host ""
    Write-Warning "Student is installed, but VPN service is not running."
    Write-Warning "Rerun Install-Student.cmd or Repair-Student-Vpn.cmd as admin."
    exit 1
}
