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

function New-StickyPortableArchive([string]$Directory, [string]$ZipPath, [string]$PackageName) {
    # .NET Framework ZipFile.CreateFromDirectory writes backslashes on Windows.
    # Emit ZIP-standard forward slashes explicitly under both PowerShell 5 and 7.
    if (Test-Path -LiteralPath $ZipPath) { throw 'Archive already exists; it is never overwritten.' }
    if ($PackageName -notmatch '^Sticky-(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-win-x64-portable$') { throw 'Invalid package root name.' }
    if (Test-Path -LiteralPath (Join-Path $Directory 'Data')) { throw 'A release archive must not contain personal data.' }
    Add-Type -AssemblyName System.IO.Compression
    $sourceRoot = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $output = [IO.File]::Open([IO.Path]::GetFullPath($ZipPath), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new($output, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
                if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Release archive cannot include file links.' }
                $relative = $file.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
                $entry = $archive.CreateEntry($PackageName + '/' + $relative, [IO.Compression.CompressionLevel]::Optimal)
                $input = [IO.File]::OpenRead($file.FullName)
                try { $entryOutput = $entry.Open(); try { $input.CopyTo($entryOutput) } finally { $entryOutput.Dispose() } }
                finally { $input.Dispose() }
            }
        } finally { $archive.Dispose() }
        $output.Flush($true)
    } finally { $output.Dispose() }
}
