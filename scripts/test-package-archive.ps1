param([switch]$Worker)
$ErrorActionPreference = 'Stop'
# Exercise the .NET Framework ZIP writer too, even when release.ps1 runs under PowerShell 7.
if (!$Worker -and $PSVersionTable.PSVersion.Major -gt 5) {
    & powershell.exe -NoProfile -File $PSCommandPath -Worker
    if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell ZIP compatibility checks failed.' }
    return
}
. "$PSScriptRoot\common.ps1"
$fixture = Join-Path $projectRoot ('.test-data\zip-format-' + [guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'source'
New-Item -ItemType Directory -Path (Join-Path $source 'tools') -Force | Out-Null
$unicodeName = ([string][char]0x6D4B) + [char]0x8BD5 + '.txt'
[IO.File]::WriteAllText((Join-Path $source ('tools\' + $unicodeName)), 'known content')
[IO.File]::WriteAllText((Join-Path $source 'portable.flag'), 'portable')
$zipPath = Join-Path $fixture 'package.zip'
$prefix = 'Sticky-0.3.0-win-x64-portable'
New-StickyPortableArchive -Directory $source -ZipPath $zipPath -PackageName $prefix
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName.Contains('\') -or !$entry.FullName.StartsWith($prefix + '/')) { throw 'ZIP path is not canonical.' }
    }
    $entry = $zip.GetEntry($prefix + '/tools/' + $unicodeName)
    if (!$entry) { throw 'Unicode filename was not preserved.' }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { if ($reader.ReadToEnd() -ne 'known content') { throw 'ZIP contents changed.' } } finally { $reader.Dispose() }
} finally { $zip.Dispose() }
Write-Output 'PASS Windows PowerShell emits standard ZIP paths and preserves Unicode file contents'
$hash = (Get-FileHash -LiteralPath $zipPath).Hash
$refused = $false
try { New-StickyPortableArchive -Directory $source -ZipPath $zipPath -PackageName $prefix } catch { $refused = $true }
if (!$refused -or (Get-FileHash -LiteralPath $zipPath).Hash -ne $hash) { throw 'Existing archive was overwritten.' }
Write-Output 'PASS Existing release archives are never overwritten'
New-Item -ItemType Directory -Path (Join-Path $source 'Data') | Out-Null
$refused = $false
try { New-StickyPortableArchive -Directory $source -ZipPath (Join-Path $fixture 'private.zip') -PackageName $prefix } catch { $refused = $true }
if (!$refused -or (Test-Path -LiteralPath (Join-Path $fixture 'private.zip'))) { throw 'Personal Data was included in a release archive.' }
Write-Output 'PASS Personal Data prevents release archive creation'
