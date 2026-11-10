param([Parameter(Mandatory = $true)][string]$TestAssembly, [string]$OutputDirectory)
. "$PSScriptRoot\common.ps1"
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('.test-data\vault-release-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Vault release validation requires a fresh isolated directory.' }
& $dotnet ([IO.Path]::GetFullPath($TestAssembly)) --vault-release-check $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "Vault release validation failed. Diagnostics: $OutputDirectory" }
$report = @(Get-Content -LiteralPath (Join-Path $OutputDirectory 'vault-release-results.json') -Raw | ConvertFrom-Json)
$expected = @('portable-password-recovery','tampering-and-failed-writes','plaintext-migration-and-sync-exclusion','shared-ui-lock-and-icons','interrupted-migration')
if ($report.Count -ne $expected.Count -or @($report | Where-Object { $_.Passed -ne $true }).Count -or
    @(Compare-Object $expected @($report | ForEach-Object { $_.Name })).Count) { throw 'Vault release validation report is incomplete or contains failures.' }
Write-Output "All 5 vault release checks passed. Report and UI screenshots: $OutputDirectory"
