[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param([string]$Version, [switch]$Login)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$repository = 'ZhangShiQi/Sticky'
$token = $null

function Read-Git([string[]]$Arguments) {
    $result = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) { throw ('Git failed: ' + ($Arguments -join ' ')) }
    return $result
}

Push-Location $projectRoot
try {
    $root = (Read-Git @('rev-parse', '--show-toplevel')) -join ''
    if ([IO.Path]::GetFullPath($root).TrimEnd('\', '/') -ne [IO.Path]::GetFullPath($projectRoot).TrimEnd('\', '/')) {
        throw 'Run this script from the Sticky repository, not a copied subdirectory.'
    }
    $props = [xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw -Encoding UTF8)
    $configuredVersion = [string]$props.Project.PropertyGroup.Version
    if (!$Version) { $Version = $configuredVersion }
    if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$' -or $Version -ne $configuredVersion) {
        throw 'Version must match the stable three-part Version in Directory.Build.props. Use set-version.ps1 first.'
    }
    $parsedVersion = [version]$Version
    $tag = "v$Version"
    $notes = Get-Content -LiteralPath 'CHANGELOG.md' -Raw -Encoding UTF8
    $section = [regex]::Match($notes, '(?ms)^## \[' + [regex]::Escape($Version) + '\][^\r\n]*\r?\n(.*?)(?=^## |\z)')
    if (!$section.Success -or !$section.Groups[1].Value.Trim()) { throw "Missing release notes for [$Version] in CHANGELOG.md." }
    $branch = (Read-Git @('symbolic-ref', '--quiet', '--short', 'HEAD')) -join ''
    if ($branch -ne 'main') { throw 'Release from main, with all intended changes committed.' }
    $urls = @(Read-Git @('remote', 'get-url', '--push', '--all', 'origin'))
    if ($urls.Count -ne 1 -or $urls[0] -notmatch '^https://github\.com/ZhangShiQi/Sticky(?:\.git)?/?$') {
        throw 'origin must push to https://github.com/ZhangShiQi/Sticky.git only.'
    }
    $fetchUrl = (Read-Git @('remote', 'get-url', 'origin')) -join ''
    if ($fetchUrl -notmatch '^https://github\.com/ZhangShiQi/Sticky(?:\.git)?/?$') { throw 'origin must fetch from the same official Sticky repository.' }
    $changes = @(Read-Git @('status', '--porcelain', '--untracked-files=normal'))
    Write-Output "Release: Sticky $Version ($tag)"
    Write-Output "Repository: https://github.com/$repository"
    Write-Output 'Plan: verify -> local tests/build/package -> push source/tag -> upload draft assets -> publish Release.'
    Write-Output 'No GitHub Actions runner or Actions artifact storage is used.'
    if ($WhatIfPreference) {
        if ($changes.Count) { Write-Warning 'Working tree has uncommitted files. A real release will refuse to run until they are committed.' }
        Write-Output 'Preview only: no fetch, tags, commits, pushes or releases. Remote permissions/tags have not been checked.'
        return
    }
    if ($changes.Count) { throw 'Working tree is not clean. Review and commit the intended files first; this script never auto-commits files.' }
    foreach ($required in @('scripts/publish-release.ps1', 'CHANGELOG.md', 'Directory.Build.props', 'scripts/package-portable.ps1')) {
        Read-Git @('cat-file', '-e', "HEAD:$required") | Out-Null
    }
    $head = (Read-Git @('rev-parse', 'HEAD')) -join ''
    $remoteTags = @(Read-Git @('ls-remote', '--tags', 'origin', 'refs/tags/v*'))
    $remoteTagCommit = $null
    $peeledCommit = $null
    foreach ($line in $remoteTags) {
        if ($line -match '^(\S+)\srefs/tags/v(\d+\.\d+\.\d+)(\^\{\})?$') {
            $sha = $Matches[1]; $remoteVersion = $Matches[2]; $peeled = $Matches[3]
            if ([version]$remoteVersion -gt $parsedVersion) { throw 'Remote has a newer version. Published version tags are never overwritten.' }
            if ($remoteVersion -eq $Version) {
                if ($peeled) { $peeledCommit = $sha } else { $remoteTagCommit = $sha }
            }
        }
    }
    if ($peeledCommit) { $remoteTagCommit = $peeledCommit }
    if ($remoteTagCommit -and $remoteTagCommit -ne $head) { throw 'Remote version tag points to another commit; it is never overwritten.' }
    $remoteBranch = @(Read-Git @('ls-remote', '--heads', 'origin', 'refs/heads/main'))
    if ($remoteBranch.Count) {
        Read-Git @('fetch', '--no-tags', 'origin', 'refs/heads/main') | Out-Null
        & git merge-base --is-ancestor FETCH_HEAD HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Remote main has changes missing locally. Integrate them before releasing; no force push is performed.' }
    }
    $localTags = @(Read-Git @('tag', '--list', $tag))
    if ($localTags.Count) {
        $tagCommit = (Read-Git @('rev-parse', "$tag^{}")) -join ''
        if ($tagCommit -ne $head) { throw "$tag already points to another local commit. It will not be moved." }
    }
    . (Join-Path $PSScriptRoot 'publish-release.ps1')
    $token = Get-ReleaseCredential -Login $Login
    Get-ReleaseRepository -Token $token | Out-Null
    $existing = Get-ReleaseState -Tag $tag -Token $token
    if ($existing -and !$existing.draft) { throw 'This version is already published; it will never be overwritten.' }
    if (!$PSCmdlet.ShouldProcess("https://github.com/$repository", "Test and package locally, push $tag and publish Release")) { return }
    $outputRoot = Join-Path $projectRoot ('.tools\releases\' + $Version + '-' + [guid]::NewGuid().ToString('N'))
    & (Join-Path $PSScriptRoot 'test-release.ps1')
    & (Join-Path $PSScriptRoot 'test-publish-release.ps1')
    & (Join-Path $projectRoot 'test.ps1') -ArtifactsPath (Join-Path $outputRoot 'test-build')
    $outputDirectory = Join-Path $outputRoot "Sticky-$Version-win-x64-portable"
    & (Join-Path $PSScriptRoot 'package-portable.ps1') -OutputDirectory $outputDirectory
    $package = $outputDirectory + '.zip'
    $manifest = Get-Content -LiteralPath (Join-Path $outputDirectory 'release-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.version -ne $Version -or $manifest.runtimeIdentifier -ne 'win-x64') { throw 'Built package version/platform does not match the release.' }
    $hash = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    $expected = ((Get-Content -LiteralPath ($package + '.sha256') -Raw).Trim() -split '\s+')[0]
    if ($hash -ne $expected) { throw 'Package checksum mismatch.' }
    if (((Read-Git @('rev-parse', 'HEAD')) -join '') -ne $head -or @(Read-Git @('status', '--porcelain', '--untracked-files=normal')).Count) {
        throw 'Source changed during testing/packaging. Commit intended changes and retry with a new package.'
    }
    if (!$localTags.Count -and !$remoteTagCommit) { Read-Git @('tag', '-a', $tag, '-m', "Sticky $Version", $head) | Out-Null }
    $pushRefs = @("${head}:refs/heads/main")
    if (!$remoteTagCommit) { $pushRefs += "refs/tags/${tag}:refs/tags/$tag" }
    & git push --atomic origin @pushRefs
    if ($LASTEXITCODE -ne 0) { throw 'Push failed. The local tag is retained for retry; no remote refs were force-updated. Verify remote state before retrying.' }
    Send-LocalRelease -Tag $tag -Version $Version -Commit $head -Package $package -Notes $section.Groups[1].Value.Trim() -Token $token
    Write-Output "Published $head as $tag using local build resources."
    Write-Output "Local package: $package"
    Write-Output "Release: https://github.com/$repository/releases/tag/$tag"
}
finally { $token = $null; Pop-Location }
