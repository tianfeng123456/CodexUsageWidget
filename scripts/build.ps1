param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
$portableDotnet = Join-Path $workspaceRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $portableDotnet) { $portableDotnet } else { 'dotnet' }
$solution = Join-Path $projectRoot 'CodexUsageWidget.sln'
$config = Join-Path $projectRoot 'NuGet.Config'
$dist = Join-Path $projectRoot 'dist'

$env:DOTNET_CLI_HOME = Join-Path $workspaceRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $workspaceRoot '.nuget\packages'
$env:APPDATA = Join-Path $workspaceRoot '.dotnet-home\appdata'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

New-Item -ItemType Directory -Force -Path $env:APPDATA | Out-Null

& $dotnet restore $solution --configfile $config
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

if (-not $SkipTests) {
    & $dotnet test $solution -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
}

$resolvedProjectRoot = [IO.Path]::GetFullPath($projectRoot)
$resolvedDist = [IO.Path]::GetFullPath($dist)
$projectRootPrefix = $resolvedProjectRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedDist.StartsWith(
        $projectRootPrefix,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean unexpected publish path: $resolvedDist"
}

if (Test-Path -LiteralPath $resolvedDist) {
    Remove-Item -LiteralPath $resolvedDist -Recurse -Force
}

& $dotnet publish (Join-Path $projectRoot 'src\CodexUsageWidget\CodexUsageWidget.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $resolvedDist
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$exe = Join-Path $resolvedDist 'CodexUsageWidget.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Publish completed without the expected executable: $exe"
}

Write-Host "Published: $exe"
