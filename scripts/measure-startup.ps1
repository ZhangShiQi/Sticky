param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [int]$Runs = 3,
    [string]$OutputDirectory,
    [switch]$Background,
    [ValidateRange(0,2000)][int]$NoteCount = 0,
    [ValidateRange(0,30)][int]$OpenNotes = 0
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ($Runs -lt 1 -or $Runs -gt 20) { throw 'Runs must be between 1 and 20.' }
if ($OpenNotes -gt $NoteCount) { throw 'OpenNotes must not exceed NoteCount.' }
$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
if (!(Test-Path -LiteralPath (Join-Path $PackageDirectory 'release-manifest.json'))) { throw 'A complete portable package is required.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('.test-data\startup-benchmark-' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Output directory already exists; nothing was changed.' }
$package = Join-Path $OutputDirectory 'portable'
New-Item -ItemType Directory -Path $package -Force | Out-Null
# Copy program components only. Never use, copy, or terminate a user's real workspace.
foreach ($name in @('Sticky.exe', 'tools', 'assets', 'licenses', 'portable.flag', 'release-manifest.json', '使用说明.txt')) {
    $source = Join-Path $PackageDirectory $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $package -Recurse }
}
$data = Join-Path $OutputDirectory 'test-data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$key = 'Sticky.Tests.' + [guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText((Join-Path $data '.sticky-update-test'), $key)
if ($NoteCount -gt 0) {
    New-Item -ItemType Directory -Path (Join-Path $data 'notes'), (Join-Path $data 'device') -Force | Out-Null
    $windows = [ordered]@{}
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    for ($index = 0; $index -lt $NoteCount; $index++) {
        $id = [guid]::NewGuid().ToString()
        $note = [ordered]@{ schemaVersion=1; id=$id; title=('Startup fixture ' + $index); color='amber'; createdAt=$now; modifiedAt=$now;
            blocks=@(@{ kind='paragraph'; alignment='Left'; marker='Disc'; inlines=@(@{kind='text';text='Synthetic startup benchmark content';fontSize=16;usesDefaultFontSize=$true}); children=@() }) }
        [IO.File]::WriteAllText((Join-Path $data ('notes\' + $id + '.json')), ($note | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        $windows[$id] = @{isOpen=($index -lt $OpenNotes);topmost=$false;left=(180 + ($index % 6)*32);top=(130 + ($index % 6)*32);width=390;height=320}
    }
    [IO.File]::WriteAllText((Join-Path $data 'device\windows.json'), (@{windows=$windows} | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}
$runtime = Join-Path $package 'tools\runtime\Sticky.exe'
$oldTrace = $env:STICKY_STARTUP_TRACE_DIR
$results = @()
try {
    for ($run = 0; $run -lt $Runs; $run++) {
        $trace = Join-Path $OutputDirectory ('trace-' + $run)
        $env:STICKY_STARTUP_TRACE_DIR = $trace
        $arguments = @('--data-dir', ('"' + $data + '"'), '--test-instance-key', $key)
        if ($Background) { $arguments += '--startup' }
        $started = [DateTimeOffset]::UtcNow
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $launcher = Start-Process -FilePath (Join-Path $package 'Sticky.exe') -ArgumentList $arguments -WindowStyle Hidden -PassThru
        try {
            if (!$launcher.WaitForExit(60000)) { throw 'Startup timed out.' }
            $watch.Stop()
            if ($launcher.ExitCode -ne 0) { throw 'Launcher failed; inspect the isolated diagnostic files.' }
            # Commit can reach the child a few milliseconds after the launcher exits on first launch.
            # Allow its final trace and initial blank-note save to settle, outside the measured interval.
            Start-Sleep -Milliseconds 300
            $events = @()
            if (Test-Path -LiteralPath $trace) {
                foreach ($file in Get-ChildItem -LiteralPath $trace -Filter '*.jsonl') {
                    $events += Get-Content -LiteralPath $file.FullName | ForEach-Object { $_ | ConvertFrom-Json }
                }
            }
            $interactive = $events | Where-Object { $_.phase -eq 'interactive' } | Select-Object -First 1
            $elapsed = if ($interactive) { [math]::Round(([DateTimeOffset]$interactive.time - $started).TotalMilliseconds) } else { $watch.ElapsedMilliseconds }
            $results += [pscustomobject]@{ Run=$run; FirstLaunch=($run -eq 0); InteractiveMs=$elapsed; LauncherExitMs=$watch.ElapsedMilliseconds; Measurement= $(if ($interactive) { 'runtime-interactive' } else { 'legacy-launcher-exit-proxy' }) }
        }
        finally {
            $launcher.Refresh()
            if (!$launcher.HasExited) { Stop-Process -Id $launcher.Id }
            $launcher.Dispose()
            Get-Process -Name Sticky -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $runtime } | Stop-Process
        }
        Start-Sleep -Milliseconds 200
    }
}
finally { $env:STICKY_STARTUP_TRACE_DIR = $oldTrace }
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize
Write-Output ('Artifacts: ' + $OutputDirectory)
