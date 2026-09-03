#Requires -RunAsAdministrator
param(
    [string] $InstallDir = "$env:ProgramFiles\KIBERone\Student"
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

Write-Host ""
Write-Host "Installation complete."
Write-Host "  App:        $installedExe"
Write-Host "  VPN repair: $serviceScriptInstalled"

if ($null -eq $vpnError) {
    Write-Host "  VPN service: KIBERoneStudentVpn (running)"
    Write-Host "Checking embedded VPN test clients ..."
    & $installedExe /verify-vpn
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Warning "Student is installed, but VPN probe check failed."
        Write-Warning "Rerun Install-Student.cmd or Repair-Student-Vpn.cmd as admin."
        exit $LASTEXITCODE
    }
} else {
    Write-Host ""
    Write-Warning "Student is installed, but VPN service is not running."
    Write-Warning "Rerun Install-Student.cmd or Repair-Student-Vpn.cmd as admin."
    exit 1
}
