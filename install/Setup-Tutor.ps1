param(
    [string] $InstallDir = ""
)

$ErrorActionPreference = "Stop"
$packageRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceDir = Join-Path $packageRoot "app"
$sourceExe = Join-Path $sourceDir "Kiberone.Tutor.exe"

if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw "Installer package is incomplete: app\Kiberone.Tutor.exe not found."
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\KIBERone\Tutor"
}

Write-Host "Installing KIBERone Tutor to $InstallDir ..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Get-ChildItem -LiteralPath $sourceDir -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $InstallDir -Recurse -Force
}

$installedExe = Join-Path $InstallDir "Kiberone.Tutor.exe"
if (-not (Test-Path -LiteralPath $installedExe)) {
    throw "Installation incomplete: $installedExe not found."
}

Write-Host "Installed KIBERone Tutor."
Write-Host "  App: $installedExe"
