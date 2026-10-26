. "$PSScriptRoot\scripts\common.ps1"
Push-Location $projectRoot
try {
    Invoke-StickyBuild
    $testDirectory = Join-Path $projectRoot ('.test-data\' + [Guid]::NewGuid().ToString('N'))
    & $dotnet (Join-Path $projectRoot 'tests\Sticky.Tests\bin\Release\net10.0-windows\Sticky.Tests.dll') $testDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Sticky tests failed.' }
} finally { Pop-Location }
