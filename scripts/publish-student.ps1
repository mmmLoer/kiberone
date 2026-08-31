$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = if (Test-Path (Join-Path $projectRoot ".dotnet\dotnet.exe")) {
    Join-Path $projectRoot ".dotnet\dotnet.exe"
} else {
    "dotnet"
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet-home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:NUGET_PACKAGES = Join-Path $projectRoot ".nuget\packages"

$out = Join-Path $projectRoot "dist\Student-win-x64"
$project = Join-Path $projectRoot "src\Kiberone.Student\Kiberone.Student.csproj"

Write-Host "Publishing Student to $out (PublishSingleFile=false for VPN native DLLs) ..."
& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $out -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$nativeSource = Join-Path $projectRoot "src\Kiberone.VpnAgent\native"
$nativeOut = Join-Path $out "native"
New-Item -ItemType Directory -Force -Path $nativeOut | Out-Null
foreach ($dll in @("tunnel.dll", "wireguard.dll")) {
    $src = Join-Path $nativeSource $dll
    if (-not (Test-Path $src)) {
        $src = Join-Path (Join-Path $projectRoot "src\Kiberone.Student\native") $dll
    }
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $nativeOut $dll) -Force
        Copy-Item $src (Join-Path $out $dll) -Force
        Write-Host "Copied $dll"
    } else {
        Write-Warning "Missing $dll - place it in src\Kiberone.Student\native before publish."
    }
}

Write-Host ""
Write-Host "Published to $out"
Write-Host "Next (admin, once per PC): .\scripts\restart-student-vpn-service.ps1 -SourceDir `"$out`""
Write-Host "Or distribute: .\dist\installers\KIBERoneStudent-Setup-*.zip"
