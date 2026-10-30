param([string]$ArtifactsPath, [switch]$NoBuild, [switch]$Native)
. "$PSScriptRoot\common.ps1"
if (!$NoBuild) { Invoke-StickyBuild -ArtifactsPath $ArtifactsPath }
$application = if ($ArtifactsPath) { Join-Path ([IO.Path]::GetFullPath($ArtifactsPath)) 'bin\Sticky.Capture.Tests\release\Sticky.Capture.Tests.exe' }
    else { Join-Path $projectRoot 'tests\Sticky.Capture.Tests\bin\Release\net10.0-windows\Sticky.Capture.Tests.exe' }
$env:DOTNET_ROOT = Split-Path -Parent $dotnet
$working = Join-Path $projectRoot 'tests\Sticky.Capture.Tests'
$suites = @('self','qr','window','ocr','translation','ai','vision','annotation','scroll','auto-scroll','scroll-optimization','scroll-permissive','recording')
if ($Native) { $suites += @('scroll-native','auto-scroll-native','recording-native','recording-ui-native') }
$report = [Collections.Generic.List[object]]::new()
foreach ($suite in $suites) {
    $process = Start-Process -FilePath $application -ArgumentList "--$suite-test" -WorkingDirectory $working -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(240000)) { $process.Kill(); throw "Capture test timed out: $suite" }
    $report.Add(@{ suite = $suite; exitCode = $process.ExitCode })
    if ($process.ExitCode -ne 0) { throw "Capture suite failed: $suite. Inspect tests/Sticky.Capture.Tests/artifacts/$suite-test.txt" }
    Write-Output "PASS capture suite: $suite"
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $working 'artifacts\suite-results.json') -Encoding UTF8
