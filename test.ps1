param([string]$ArtifactsPath, [switch]$NativeCapture)
. "$PSScriptRoot\scripts\common.ps1"
Push-Location $projectRoot
try {
    if ($ArtifactsPath) { $ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath) }
    Invoke-StickyBuild -ArtifactsPath $ArtifactsPath
    $testDirectory = Join-Path $projectRoot ('.test-data\' + [Guid]::NewGuid().ToString('N'))
    $testAssembly = if ($ArtifactsPath) { Join-Path $ArtifactsPath 'bin\Sticky.Tests\release\Sticky.Tests.dll' }
        else { Join-Path $projectRoot 'tests\Sticky.Tests\bin\Release\net10.0-windows\Sticky.Tests.dll' }
    & $dotnet $testAssembly $testDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Sticky tests failed.' }
    & (Join-Path $projectRoot 'scripts\test-capture.ps1') -NoBuild -ArtifactsPath $ArtifactsPath -Native:$NativeCapture
} finally { Pop-Location }
