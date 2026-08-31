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

$out = Join-Path $projectRoot "dist\Tutor-win-x64"
$project = Join-Path $projectRoot "src\Kiberone.Tutor\Kiberone.Tutor.csproj"

Write-Host "Publishing Tutor to $out (PublishSingleFile=false for SkiaSharp native DLLs) ..."
& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $out -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Published to $out"
