param([string]$DataDirectory, [switch]$NoBuild)
. "$PSScriptRoot\scripts\common.ps1"
Push-Location $projectRoot
try {
    if (!$NoBuild) { Invoke-StickyBuild }
    $application = Join-Path $projectRoot 'src\Sticky.App\bin\Release\net10.0-windows\Sticky.dll'
    if ($DataDirectory) {
        & $dotnet $application --data-dir ([System.IO.Path]::GetFullPath($DataDirectory))
    } else {
        & $dotnet $application
    }
    if ($LASTEXITCODE -ne 0) { throw "Sticky exited with code $LASTEXITCODE" }
} finally { Pop-Location }
