param(
    [string] $InstallDir = "$env:ProgramFiles\KIBERone\Student",
    [string] $ShortcutName = "KIBERone Student",
    [string] $DesktopPath = ""
)

$ErrorActionPreference = "Stop"

$installedExe = [System.IO.Path]::GetFullPath((Join-Path $InstallDir "Kiberone.Student.exe"))
if (-not (Test-Path -LiteralPath $installedExe)) {
    throw "Student is not installed: $installedExe"
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
$shortcut.Description = "KIBERone Classroom Student"
$shortcut.Save()

Write-Host "Shortcut: $shortcutPath"
Write-Host "Target:   $installedExe"
