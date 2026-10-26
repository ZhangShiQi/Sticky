param([Parameter(Mandatory = $true)][string]$Package, [Parameter(Mandatory = $true)][string]$TestAssembly)
. "$PSScriptRoot\common.ps1"
$fixture = Join-Path $projectRoot ('.test-data\portable-update-' + [guid]::NewGuid().ToString('N'))
& $dotnet ([IO.Path]::GetFullPath($TestAssembly)) --portable-update-smoke ([IO.Path]::GetFullPath($Package)) $fixture
if ($LASTEXITCODE -ne 0) { throw "Real portable update checks failed. Diagnostics retained at $fixture" }
Write-Output "Portable update checks passed. Diagnostics: $fixture"
