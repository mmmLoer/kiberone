$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget\packages'
& $dotnet build (Join-Path $projectRoot 'Kiberone.sln') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet test (Join-Path $projectRoot 'tests\Kiberone.Tests\Kiberone.Tests.csproj') -c Release --no-build --no-restore
exit $LASTEXITCODE
