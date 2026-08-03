$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
$portableDotnet = Join-Path $workspaceRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $portableDotnet) { $portableDotnet } else { 'dotnet' }

$env:DOTNET_CLI_HOME = Join-Path $workspaceRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspaceRoot '.nuget\packages'
$env:APPDATA = Join-Path $workspaceRoot '.dotnet-home\appdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null

& $dotnet restore (Join-Path $projectRoot 'CodexUsageWidget.sln') `
    --configfile (Join-Path $projectRoot 'NuGet.Config')
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

& $dotnet test (Join-Path $projectRoot 'CodexUsageWidget.sln') `
    -c Release `
    --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
