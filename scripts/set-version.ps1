param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
    throw 'Use a stable three-part version, for example 0.2.1.'
}
$parsedVersion = [version]$Version
$props = Join-Path (Split-Path -Parent $PSScriptRoot) 'Directory.Build.props'
$source = [System.IO.File]::ReadAllText($props)
if ([regex]::Matches($source, '<Version>[^<]+</Version>').Count -ne 1) { throw 'Expected exactly one shared Version property.' }
$source = [regex]::Replace($source, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
[System.IO.File]::WriteAllText($props, $source, [System.Text.UTF8Encoding]::new($false))
Write-Output "Version set to $Version. Add release notes to CHANGELOG.md before tagging v$Version."
