param([switch]$Run, [switch]$Check)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget'
dotnet build (Join-Path $PSScriptRoot 'TheGarden.slnx') -c Release --nologo --disable-build-servers -m:1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Check) {
    dotnet run --project (Join-Path $PSScriptRoot 'tests/TheGarden.Checks') -c Release --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet run --project (Join-Path $PSScriptRoot 'tests/TheGarden.DesktopChecks') -c Release --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
$app = Join-Path $PSScriptRoot 'src/TheGarden.Desktop/bin/Release/net10.0-windows/Map.exe'
if ($Run) { Start-Process -FilePath $app -WorkingDirectory $PSScriptRoot -WindowStyle Normal }
