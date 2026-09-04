$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = "0.10.13"
$installersDir = Join-Path $projectRoot "dist\installers"
$stagingRoot = Join-Path $installersDir "_staging"

function Ensure-NativeDlls {
    param([string] $Root)
    $native = Join-Path $Root "src\Kiberone.VpnAgent\native"
    New-Item -ItemType Directory -Force -Path $native | Out-Null
    foreach ($dll in @("tunnel.dll", "wireguard.dll")) {
        if (Test-Path (Join-Path $native $dll)) { continue }
        $fallback = Join-Path $Root "dist\Student-win-x64\native\$dll"
        if (Test-Path $fallback) {
            Copy-Item $fallback (Join-Path $native $dll) -Force
            Write-Host "Restored $dll from previous dist build."
        } else {
            throw "Missing $dll. Place tunnel.dll and wireguard.dll in src\Kiberone.VpnAgent\native\"
        }
    }
}

function New-StudentInstaller {
    param([string] $PublishDir, [string] $ZipPath)
    $stage = Join-Path $stagingRoot "student"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage "app") | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $stage "service") | Out-Null

    Copy-Item -Path (Join-Path $PublishDir "*") -Destination (Join-Path $stage "app") -Recurse -Force
    Copy-Item (Join-Path $projectRoot "install\Setup-Student.ps1") $stage -Force
    Copy-Item (Join-Path $projectRoot "install\Install-Student.cmd") $stage -Force
    Copy-Item (Join-Path $projectRoot "install\Create-Student-Shortcut.ps1") $stage -Force
    Copy-Item (Join-Path $projectRoot "install\Repair-Student-Vpn.cmd") $stage -Force
    Copy-Item (Join-Path $projectRoot "install\README-Student.txt") $stage -Force
    Copy-Item (Join-Path $projectRoot "scripts\install-student-vpn-service.ps1") (Join-Path $stage "service") -Force

    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $ZipPath -CompressionLevel Optimal
}

function New-TutorInstaller {
    param([string] $PublishDir, [string] $ZipPath)
    $stage = Join-Path $stagingRoot "tutor"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Join-Path $stage "app") | Out-Null

    Copy-Item -Path (Join-Path $PublishDir "*") -Destination (Join-Path $stage "app") -Recurse -Force
    Copy-Item (Join-Path $projectRoot "install\Setup-Tutor.ps1") $stage -Force
    Copy-Item (Join-Path $projectRoot "install\Install-Tutor.cmd") $stage -Force
    Copy-Item (Join-Path $projectRoot "install\Create-Tutor-Shortcut.ps1") $stage -Force
    Copy-Item (Join-Path $projectRoot "install\README-Tutor.txt") $stage -Force

    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $ZipPath -CompressionLevel Optimal
}

function Update-StudentManifest {
    param([string] $StudentExe)
    $updatesDir = Join-Path $projectRoot "updates"
    New-Item -ItemType Directory -Force -Path $updatesDir | Out-Null
    $dest = Join-Path $updatesDir "KIBERoneStudent.exe"
    Copy-Item $StudentExe $dest -Force
    $bytes = [IO.File]::ReadAllBytes($dest)
    $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace("-", "").ToLowerInvariant()
    $manifest = @{
        version      = $version
        filename     = "KIBERoneStudent.exe"
        size         = $bytes.Length
        sha256       = $hash
        published_at = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    }
    $manifest | ConvertTo-Json | Set-Content (Join-Path $updatesDir "student_manifest.json") -Encoding UTF8
}

Write-Host "=== KIBERone release build v$version ===" -ForegroundColor Cyan
Ensure-NativeDlls -Root $projectRoot

Get-Process -Name "Kiberone.Student", "Kiberone.Tutor" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

& (Join-Path $projectRoot "scripts\publish-student.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Single-file artifact for Tutor→Student update channel (must replace one exe; folder publish apphost is ~150KB and useless).
$studentUpdateOut = Join-Path $projectRoot "dist\Student-update-win-x64"
Write-Host "Publishing single-file Student update package to $studentUpdateOut ..."
$dotnet = if (Test-Path (Join-Path $projectRoot ".dotnet\dotnet.exe")) { Join-Path $projectRoot ".dotnet\dotnet.exe" } else { "dotnet" }
& $dotnet publish (Join-Path $projectRoot "src\Kiberone.Student\Kiberone.Student.csproj") `
    -c Release -r win-x64 --self-contained true `
    -o $studentUpdateOut `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$nativeSrc = Join-Path $projectRoot "src\Kiberone.VpnAgent\native"
foreach ($dll in @("tunnel.dll", "wireguard.dll")) {
    $src = Join-Path $nativeSrc $dll
    if (Test-Path $src) { Copy-Item $src (Join-Path $studentUpdateOut $dll) -Force }
}

& (Join-Path $projectRoot "scripts\publish-tutor.ps1")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$studentPublish = Join-Path $projectRoot "dist\Student-win-x64"
$tutorPublish = Join-Path $projectRoot "dist\Tutor-win-x64"
New-Item -ItemType Directory -Force -Path $installersDir | Out-Null

$studentZip = Join-Path $installersDir "KIBERoneStudent-Setup-$version-win-x64.zip"
$tutorZip = Join-Path $installersDir "KIBERoneTutor-Setup-$version-win-x64.zip"
New-StudentInstaller -PublishDir $studentPublish -ZipPath $studentZip
New-TutorInstaller -PublishDir $tutorPublish -ZipPath $tutorZip

$studentUpdateExe = Join-Path $studentUpdateOut "Kiberone.Student.exe"
Copy-Item $studentUpdateExe (Join-Path $projectRoot "KIBERoneStudent.exe") -Force
Copy-Item (Join-Path $tutorPublish "Kiberone.Tutor.exe") (Join-Path $projectRoot "KIBERoneTutor.exe") -Force
Update-StudentManifest -StudentExe $studentUpdateExe

# Tutor serves updates from BaseDirectory\updates — keep a copy next to the Tutor publish.
$tutorUpdates = Join-Path $tutorPublish "updates"
New-Item -ItemType Directory -Force -Path $tutorUpdates | Out-Null
Copy-Item (Join-Path $projectRoot "updates\KIBERoneStudent.exe") (Join-Path $tutorUpdates "KIBERoneStudent.exe") -Force
Copy-Item (Join-Path $projectRoot "updates\student_manifest.json") (Join-Path $tutorUpdates "student_manifest.json") -Force

if (Test-Path $stagingRoot) { Remove-Item $stagingRoot -Recurse -Force }

Write-Host ""
Write-Host "Installers:" -ForegroundColor Green
Get-Item $studentZip, $tutorZip | Select-Object Name, @{ N = "SizeMB"; E = { [math]::Round($_.Length / 1MB, 1) } }, LastWriteTime
Write-Host ""
Write-Host "Portable builds:"
Write-Host "  $studentPublish"
Write-Host "  $tutorPublish"
