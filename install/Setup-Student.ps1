#Requires -RunAsAdministrator
param(
    [string] $InstallDir = "$env:ProgramFiles\KIBERone\Student"
)

$ErrorActionPreference = "Stop"
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDir = Join-Path $packageRoot "app"
$serviceScript = Join-Path $packageRoot "service\install-student-vpn-service.ps1"

if (-not (Test-Path -LiteralPath (Join-Path $sourceDir "Kiberone.Student.exe"))) {
    throw "Installer package is incomplete: app\Kiberone.Student.exe not found."
}

if (-not (Test-Path -LiteralPath $serviceScript)) {
    throw "Installer package is incomplete: service\install-student-vpn-service.ps1 not found."
}

Write-Host "Installing KIBERone Student to $InstallDir ..."
& $serviceScript -SourceDir $sourceDir -InstallDir $InstallDir

$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "KIBERone Student.lnk"
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $InstallDir "Kiberone.Student.exe"
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = "KIBERone Classroom Student"
$shortcut.Save()

Write-Host ""
Write-Host "Installation complete."
Write-Host "  App:     $InstallDir\Kiberone.Student.exe"
Write-Host "  Shortcut: $shortcutPath"
Write-Host "  VPN service: KIBERoneStudentVpn (one-time setup, no UAC during lessons)"
