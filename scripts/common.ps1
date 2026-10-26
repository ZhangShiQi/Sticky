$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnet = $localDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $dotnet
} else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
}
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
function Invoke-StickyBuild([string]$ArtifactsPath) {
    $buildArguments = @('build', (Join-Path $projectRoot 'Sticky.sln'), '-c', 'Release', '-m:1', '-nr:false', '-p:UseSharedCompilation=false', '--ignore-failed-sources', '--nologo')
    if ($ArtifactsPath) { $buildArguments += @('--artifacts-path', $ArtifactsPath) }
    & $dotnet @buildArguments
    if ($LASTEXITCODE -ne 0) { throw 'Sticky build failed. Install the .NET 10 SDK or check the output above.' }
}
