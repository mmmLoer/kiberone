param(
    [string] $InstallDir = "",
    [string] $ShortcutName = "KIBERone Tutor",
    [string] $DesktopPath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\KIBERone\Tutor"
}

$installedExe = [System.IO.Path]::GetFullPath((Join-Path $InstallDir "Kiberone.Tutor.exe"))
if (-not (Test-Path -LiteralPath $installedExe)) {
    throw "Tutor is not installed: $installedExe"
}

if ([string]::IsNullOrWhiteSpace($DesktopPath)) {
    $DesktopPath = [Environment]::GetFolderPath("Desktop")
    if ([string]::IsNullOrWhiteSpace($DesktopPath)) {
        $DesktopPath = Join-Path $env:USERPROFILE "Desktop"
    }
}

New-Item -ItemType Directory -Force -Path $DesktopPath | Out-Null

$shortcutPath = Join-Path $DesktopPath "$ShortcutName.lnk"
$workDir = [System.IO.Path]::GetFullPath($InstallDir)

$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $workDir
$shortcut.Description = "KIBERone Classroom Tutor"
$shortcut.Save()

Write-Host "Shortcut: $shortcutPath"
Write-Host "Target:   $installedExe"
