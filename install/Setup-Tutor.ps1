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

$desktop = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktop "KIBERone Tutor.lnk"
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $InstallDir "Kiberone.Tutor.exe"
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Description = "KIBERone Classroom Tutor"
$shortcut.Save()

Write-Host "Installed KIBERone Tutor."
Write-Host "  App:      $InstallDir\Kiberone.Tutor.exe"
Write-Host "  Shortcut: $shortcutPath"
