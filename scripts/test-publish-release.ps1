$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'publish-release.ps1')
$fixture = Join-Path (Split-Path -Parent $PSScriptRoot) ('.test-data\release-api-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$package = Join-Path $fixture 'Sticky-0.2.0-win-x64-portable.zip'
[IO.File]::WriteAllText($package, 'fixture package')
[IO.File]::WriteAllText(($package + '.sha256'), (Get-FileHash -LiteralPath $package).Hash.ToLowerInvariant())
$global:StickyPublishTestState = $null
function Reset-Publisher {
    $global:StickyPublishTestState = @{ Release = $null; Requests = [Collections.Generic.List[string]]::new(); BadDigest = $false; NextId = 10; Published = $false }
}
# All HTTP calls are intercepted. No credential lookup or network requests are performed.
function Invoke-RestMethod {
    param($Uri,$Method,$Headers,$Body,$InFile,$ContentType,$ErrorAction)
    $global:StickyPublishTestState.Requests.Add("$Method $Uri")
    if ($Headers.Authorization -ne 'Bearer fixture-only') { throw 'Authentication header missing.' }
    if ($Uri -eq 'https://api.github.com/repos/ZhangShiQi/Sticky') { return @{ full_name = 'ZhangShiQi/Sticky' } }
    if ($Method -eq 'GET' -and $Uri -like '*/releases/tags/*') { return $global:StickyPublishTestState.Release }
    if ($Method -eq 'POST' -and $Uri -eq 'https://api.github.com/repos/ZhangShiQi/Sticky/releases') {
        $fields = [Text.Encoding]::UTF8.GetString($Body) | ConvertFrom-Json
        if (!$fields.draft -or $fields.tag_name -ne 'v0.2.0' -or $fields.body -ne '中文更新说明') { throw 'Incorrect draft or Unicode notes.' }
        $global:StickyPublishTestState.Release = @{ id = 7; draft = $true; assets = @() }
        return $global:StickyPublishTestState.Release
    }
    if ($Method -eq 'DELETE') {
        if (!$global:StickyPublishTestState.Release.draft) { throw 'Attempted to delete published assets.' }
        $id = [int]($Uri -split '/')[-1]
        $global:StickyPublishTestState.Release.assets = @($global:StickyPublishTestState.Release.assets | Where-Object { $_.id -ne $id })
        return
    }
    if ($Method -eq 'POST' -and $Uri -like 'https://uploads.github.com/*') {
        if (!$global:StickyPublishTestState.Release.draft) { throw 'Attempted to upload to published release.' }
        $hash = (Get-FileHash -LiteralPath $InFile).Hash.ToLowerInvariant()
        if ($global:StickyPublishTestState.BadDigest) { $hash = 'bad' }
        $asset = @{ id = $global:StickyPublishTestState.NextId++; name = [IO.Path]::GetFileName($InFile); digest = 'sha256:' + $hash }
        $global:StickyPublishTestState.Release.assets += $asset
        return $asset
    }
    if ($Method -eq 'PATCH') {
        $fields = [Text.Encoding]::UTF8.GetString($Body) | ConvertFrom-Json
        if ($global:StickyPublishTestState.Release.assets.Count -ne 2 -or $fields.draft -or $fields.make_latest -ne 'true') { throw 'Published before assets completed.' }
        $global:StickyPublishTestState.Release.draft = $false
        $global:StickyPublishTestState.Published = $true
        return $global:StickyPublishTestState.Release
    }
    throw "Unexpected mocked HTTP request: $Method $Uri"
}
function Send-Fixture { Send-LocalRelease -Tag 'v0.2.0' -Version '0.2.0' -Commit 'fixture-commit' -Package $package -Notes '中文更新说明' -Token 'fixture-only' }
function Check-Publish([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Refuse-Publish([string]$Message) {
    $errorText = $null
    try { Send-Fixture } catch { $errorText = $_.Exception.Message }
    Check-Publish ($errorText -and $errorText.Contains($Message)) "Expected '$Message', got '$errorText'."
}
function git {
    $command = $args -join ' '
    $mode = $env:GCM_INTERACTIVE
    $global:StickyPublishTestState.CredentialAttempts.Add("$command [$mode]")
    $global:LASTEXITCODE = 128
    if (($command -eq '-c credential.useHttpPath=false credential fill' -and !$global:StickyPublishTestState.NoSavedCredential) -or
        $mode -eq 'always') {
        $global:LASTEXITCODE = 0
        return 'password=fixture-host-login'
    }
}
try {
    Reset-Publisher
    Get-ReleaseRepository -Token 'fixture-only' | Out-Null
    Send-Fixture
    Check-Publish $global:StickyPublishTestState.Published 'Valid draft was not published.'
    Check-Publish ($global:StickyPublishTestState.Requests[-1] -like 'PATCH *') 'Publication happened before uploads.'
    Write-Output 'PASS Both assets and their digests are verified before the draft becomes the latest Release'

    $count = $global:StickyPublishTestState.Requests.Count
    Refuse-Publish 'already published'
    Check-Publish ($global:StickyPublishTestState.Requests.Count -eq $count + 1) 'Published release received a mutating request.'
    Write-Output 'PASS Published Release assets are never modified'

    Reset-Publisher; $global:StickyPublishTestState.BadDigest = $true
    Refuse-Publish 'digest does not match'
    Check-Publish ($global:StickyPublishTestState.Release.draft -and !$global:StickyPublishTestState.Published) 'Corrupt upload was published.'
    $global:StickyPublishTestState.BadDigest = $false
    Send-Fixture
    Check-Publish $global:StickyPublishTestState.Published 'Draft retry failed.'
    Check-Publish (@($global:StickyPublishTestState.Requests | Where-Object { $_ -like 'DELETE *' }).Count -eq 1) 'Retry did not replace only the incomplete draft asset.'
    Write-Output 'PASS Digest failures leave a retryable draft and never publish incomplete files'

    $count = $global:StickyPublishTestState.Requests.Count
    $refused = $false
    try { Invoke-ReleaseApi -Url 'https://example.com/steal' -Token 'fixture-only' } catch { $refused = $true }
    Check-Publish ($refused -and $global:StickyPublishTestState.Requests.Count -eq $count) 'Credential request was allowed outside the official repository.'
    Write-Output 'PASS Publisher rejects URLs outside the official GitHub repository'
    $global:StickyPublishTestState.CredentialAttempts = [Collections.Generic.List[string]]::new()
    $global:StickyPublishTestState.NoSavedCredential = $false
    $credential = Get-ReleaseCredential -Login $true
    Check-Publish ($credential -eq 'fixture-host-login' -and $global:StickyPublishTestState.CredentialAttempts.Count -eq 2) 'Host-level saved login was not reused.'
    Check-Publish (@($global:StickyPublishTestState.CredentialAttempts | Where-Object { $_ -like '*always*' }).Count -eq 0) 'Existing host login unnecessarily prompted for authentication.'
    $credential = $null
    Write-Output 'PASS Existing host-level GitHub credentials are reused without prompting'

    $global:StickyPublishTestState.CredentialAttempts.Clear()
    $global:StickyPublishTestState.NoSavedCredential = $true
    $credential = Get-ReleaseCredential -Login $true
    Check-Publish ($credential -eq 'fixture-host-login' -and $global:StickyPublishTestState.CredentialAttempts.Count -eq 3) 'Explicit login did not follow noninteractive credential lookups.'
    $credential = $null
    Write-Output 'PASS Interactive login is used only when explicitly requested and saved credentials are missing'
    Write-Output '6 publisher API/credential checks passed; all HTTP and credential operations were mocked.'
}
finally { Remove-Variable StickyPublishTestState -Scope Global -ErrorAction SilentlyContinue }
