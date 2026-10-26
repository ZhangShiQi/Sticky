$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $projectRoot ('.test-data\release-script-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release.ps1') -Destination (Join-Path $fixture 'scripts\release.ps1')
[IO.File]::WriteAllText((Join-Path $fixture 'Directory.Build.props'), '<Project><PropertyGroup><Version>0.2.0</Version></PropertyGroup></Project>')
[IO.File]::WriteAllText((Join-Path $fixture 'CHANGELOG.md'), "## [0.2.0]`n`nFixture release notes.`n")
[IO.File]::WriteAllText((Join-Path $fixture 'scripts\publish-release.ps1'), @'
function Get-ReleaseCredential([bool]$Login) { $global:StickyReleaseTestState.Events.Add('credentials'); return 'fixture-only' }
function Get-ReleaseRepository([string]$Token) { return @{ full_name = 'ZhangShiQi/Sticky' } }
function Get-ReleaseState([string]$Tag, [string]$Token) {
    if ($global:StickyReleaseTestState.ReleasePublished) { return @{ draft = $false } }
    if ($global:StickyReleaseTestState.Draft) { return @{ draft = $true } }
    return $null
}
function Send-LocalRelease {
    param($Tag,$Version,$Commit,$Package,$Notes,$Token)
    $global:StickyReleaseTestState.Events.Add('upload')
    $global:StickyReleaseTestState.Draft = $true
    if ($global:StickyReleaseTestState.FailUpload) { throw 'Simulated upload failure' }
    $global:StickyReleaseTestState.ReleasePublished = $true
}
'@)
[IO.File]::WriteAllText((Join-Path $fixture 'test.ps1'), @'
$global:StickyReleaseTestState.Events.Add('tests')
if ($global:StickyReleaseTestState.FailTests) { throw 'Simulated test failure' }
'@)
[IO.File]::WriteAllText((Join-Path $fixture 'scripts\test-release.ps1'), '# Fixture publication checks do not recurse.')
[IO.File]::WriteAllText((Join-Path $fixture 'scripts\test-publish-release.ps1'), '# Fixture API checks do not recurse.')
[IO.File]::WriteAllText((Join-Path $fixture 'scripts\test-package-archive.ps1'), '# Fixture archive checks do not recurse.')
[IO.File]::WriteAllText((Join-Path $fixture 'scripts\test-portable-update.ps1'), @'
param($Package,$TestAssembly)
$global:StickyReleaseTestState.Events.Add('portable-tests')
if ($global:StickyReleaseTestState.FailPortable) { throw 'Simulated portable update test failure' }
'@)
[IO.File]::WriteAllText((Join-Path $fixture 'scripts\package-portable.ps1'), @'
param($OutputDirectory)
$global:StickyReleaseTestState.Events.Add('package')
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
[IO.File]::WriteAllText(($OutputDirectory + '.zip'), 'fixture zip')
$hash = (Get-FileHash -LiteralPath ($OutputDirectory + '.zip')).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($OutputDirectory + '.zip.sha256'), $hash + ' *fixture.zip')
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'release-manifest.json'), '{"version":"0.2.0","runtimeIdentifier":"win-x64"}')
'@)
$global:StickyReleaseTestRoot = $fixture
$script:cases = 0
$global:StickyReleaseTestState = $null
$originalTestFilter = $env:STICKY_TEST_FILTER
$env:STICKY_TEST_FILTER = $null
function Reset-State {
    $global:StickyReleaseTestState = @{ Calls = [Collections.Generic.List[string]]::new(); Dirty = $false; RemoteVersion = '0.1.9';
        LocalTag = $false; OtherCommit = $false; RemoteAhead = $false; FailPush = $false; Branch = 'main';
        Events = [Collections.Generic.List[string]]::new(); ReleasePublished = $false; Draft = $false; RemoteSameHead = $false; FailTests = $false; FailUpload = $false; FailPortable = $false }
}
# A scoped Git test double: no real Git command, credential lookup or network request is executed.
function git {
    $command = $args -join ' '
    $global:StickyReleaseTestState.Calls.Add($command)
    $global:LASTEXITCODE = 0
    switch -Wildcard ($command) {
        'rev-parse --show-toplevel' { return $global:StickyReleaseTestRoot }
        'symbolic-ref --quiet --short HEAD' { return $global:StickyReleaseTestState.Branch }
        'remote get-url --push --all origin' { return 'https://github.com/ZhangShiQi/Sticky.git' }
        'remote get-url origin' { return 'https://github.com/ZhangShiQi/Sticky.git' }
        'status --porcelain --untracked-files=normal' { if ($global:StickyReleaseTestState.Dirty) { return ' M src/changed.cs' }; return }
        'cat-file -e HEAD:*' { return }
        'rev-parse HEAD' { return '1111111111111111111111111111111111111111' }
        'ls-remote --tags origin refs/tags/v*' {
            $sha = if ($global:StickyReleaseTestState.RemoteSameHead) { '1111111111111111111111111111111111111111' } else { '2222222222222222222222222222222222222222' }
            return ($sha + "`trefs/tags/v" + $global:StickyReleaseTestState.RemoteVersion)
        }
        'ls-remote --heads origin refs/heads/main' { return "2222222222222222222222222222222222222222`trefs/heads/main" }
        'fetch --no-tags origin refs/heads/main' { return }
        'merge-base --is-ancestor FETCH_HEAD HEAD' { if ($global:StickyReleaseTestState.RemoteAhead) { $global:LASTEXITCODE = 1 }; return }
        'tag --list v0.2.0' { if ($global:StickyReleaseTestState.LocalTag) { return 'v0.2.0' }; return }
        'rev-parse v0.2.0^{}' { if ($global:StickyReleaseTestState.OtherCommit) { return '3333333333333333333333333333333333333333' }; return '1111111111111111111111111111111111111111' }
        'tag -a v0.2.0 -m Sticky 0.2.0 1111111111111111111111111111111111111111' { $global:StickyReleaseTestState.LocalTag = $true; return }
        'push --atomic origin 1111111111111111111111111111111111111111:refs/heads/main*' {
            $global:StickyReleaseTestState.Events.Add('push')
            if ($global:StickyReleaseTestState.FailPush) { $global:LASTEXITCODE = 1 }
            else { $global:StickyReleaseTestState.RemoteVersion = '0.2.0'; $global:StickyReleaseTestState.RemoteSameHead = $true }
            return
        }
        default { throw "Unexpected mocked Git command: $command" }
    }
}
function Assert-Release([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Invoke-Fixture([bool]$Preview = $false) {
    & (Join-Path $fixture 'scripts\release.ps1') -WhatIf:$Preview -WarningAction SilentlyContinue | Out-Null
}
function Expect-Refusal([string]$Message) {
    $errorText = $null
    try { Invoke-Fixture } catch { $errorText = $_.Exception.Message }
    Assert-Release ($null -ne $errorText -and $errorText.Contains($Message)) "Expected refusal containing '$Message', got '$errorText'."
}
function Pass([string]$Name) { $script:cases++; Write-Output "PASS $Name" }

try {
Reset-State; $global:StickyReleaseTestState.Dirty = $true
Invoke-Fixture $true
Assert-Release (@($global:StickyReleaseTestState.Calls | Where-Object { $_ -match '^(fetch|tag |push |ls-remote)' }).Count -eq 0) 'Preview mutated Git or accessed the remote.'
Assert-Release ($global:StickyReleaseTestState.Events.Count -eq 0) 'Preview accessed credentials or ran builds/uploads.'
Pass 'Preview never fetches, tags, commits or pushes, including a dirty workspace'

Reset-State; $global:StickyReleaseTestState.Dirty = $true
Expect-Refusal 'not clean'
Assert-Release (!$global:StickyReleaseTestState.LocalTag) 'Dirty worktree created a version tag.'
Pass 'Uncommitted files block publication'

Reset-State; $env:STICKY_TEST_FILTER = 'Update'
Expect-Refusal 'complete test suite is required'
Assert-Release ($global:StickyReleaseTestState.Events.Count -eq 0 -and !$global:StickyReleaseTestState.LocalTag) 'Filtered release read credentials, built or tagged.'
$env:STICKY_TEST_FILTER = $null
Pass 'A filtered test run can never authorize a release'

Reset-State; $global:StickyReleaseTestState.RemoteVersion = '0.2.0'
Expect-Refusal 'another commit'
Reset-State; $global:StickyReleaseTestState.RemoteVersion = '0.10.0'
Expect-Refusal 'never overwritten'
Pass 'Existing and numerically newer remote versions cannot be overwritten'

Reset-State; $global:StickyReleaseTestState.RemoteAhead = $true
Expect-Refusal 'Remote main has changes'
Assert-Release (!$global:StickyReleaseTestState.LocalTag) 'Diverged main created a tag.'
Pass 'Missing remote commits block publication'

Reset-State; $global:StickyReleaseTestState.LocalTag = $true; $global:StickyReleaseTestState.OtherCommit = $true
Expect-Refusal 'another local commit'
Pass 'An existing local tag is never moved to another commit'

Reset-State; $global:StickyReleaseTestState.ReleasePublished = $true; $global:StickyReleaseTestState.RemoteVersion = '0.2.0'; $global:StickyReleaseTestState.RemoteSameHead = $true
Expect-Refusal 'already published'
Assert-Release (!$global:StickyReleaseTestState.Events.Contains('package')) 'Published release was rebuilt.'
Pass 'Published releases are never rebuilt or overwritten'

Reset-State; $global:StickyReleaseTestState.FailTests = $true
Expect-Refusal 'Simulated test failure'
Assert-Release (!$global:StickyReleaseTestState.LocalTag -and !$global:StickyReleaseTestState.Events.Contains('push') -and !$global:StickyReleaseTestState.Events.Contains('upload')) 'Failed tests pushed or published files.'
Pass 'Local test failures stop before tagging, pushing or uploading'

Reset-State; $global:StickyReleaseTestState.FailPortable = $true
Expect-Refusal 'Simulated portable update test failure'
Assert-Release (!$global:StickyReleaseTestState.LocalTag -and !$global:StickyReleaseTestState.Events.Contains('push') -and !$global:StickyReleaseTestState.Events.Contains('upload')) 'Failed real portable checks pushed or published files.'
Pass 'Real portable update failures block tags, push and publication'

Reset-State; $global:StickyReleaseTestState.FailPush = $true
Expect-Refusal 'Push failed'
Assert-Release $global:StickyReleaseTestState.LocalTag 'Push failure removed the retryable local tag.'
$global:StickyReleaseTestState.FailPush = $false
Invoke-Fixture
Assert-Release (@($global:StickyReleaseTestState.Calls | Where-Object { $_ -like 'tag -a *' }).Count -eq 1) 'Retry recreated or moved the tag.'
Pass 'Failed push can be retried with the same local tag'

Reset-State; $global:StickyReleaseTestState.FailUpload = $true
Expect-Refusal 'Simulated upload failure'
$global:StickyReleaseTestState.FailUpload = $false
Invoke-Fixture
Assert-Release $global:StickyReleaseTestState.ReleasePublished 'Draft upload could not be resumed after the version tag was pushed.'
Pass 'A draft can be resumed after a successful tag push and failed asset upload'

Reset-State
Invoke-Fixture
Assert-Release ($global:StickyReleaseTestState.Calls.Contains('push --atomic origin 1111111111111111111111111111111111111111:refs/heads/main refs/tags/v0.2.0:refs/tags/v0.2.0')) 'Branch and tag were not pushed atomically from the same commit.'
Assert-Release (@($global:StickyReleaseTestState.Calls | Where-Object { $_ -match '(^| )(--force|--force-with-lease|commit|add|delete)( |$)' }).Count -eq 0) 'Release performed an unexpected write.'
Pass 'Clean releases push exactly main and the version tag without force'
Assert-Release (($global:StickyReleaseTestState.Events -join ',') -eq 'credentials,tests,package,portable-tests,push,upload') 'Local publish operations ran in the wrong order.'
Pass 'Tests and local packaging complete before push and upload'

Write-Output "$script:cases release workflow checks passed. No real repository was changed."
}
finally { $env:STICKY_TEST_FILTER = $originalTestFilter; Remove-Variable StickyReleaseTestState,StickyReleaseTestRoot -Scope Global -ErrorAction SilentlyContinue }
