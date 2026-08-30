$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot ".dotnet\dotnet.exe"
$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet-home"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:NUGET_PACKAGES = Join-Path $projectRoot ".nuget\packages"

$out = Join-Path $projectRoot "dist\VpnAgent-win-x64"
$project = Join-Path $projectRoot "src\Kiberone.VpnAgent\Kiberone.VpnAgent.csproj"

& $dotnet publish $project -c Release -r win-x64 --self-contained false -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$native = Join-Path $projectRoot "src\Kiberone.VpnAgent\native"
foreach ($dll in @("tunnel.dll", "wireguard.dll")) {
    $src = Join-Path $native $dll
    if (Test-Path $src) {
        Copy-Item $src (Join-Path $out $dll) -Force
        Write-Host "Copied $dll"
    } else {
        Write-Warning "Missing $src — place $dll into publish folder before install."
    }
}

Write-Host "Published to $out"
Write-Host "Next: .\scripts\install-vpn-agent.ps1 -SourceDir `"$out`" -ApiToken `"...`" -PeerConf `"path\to\peer.conf`""
