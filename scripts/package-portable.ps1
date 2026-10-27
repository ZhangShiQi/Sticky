param([string]$OutputDirectory, [switch]$ReadyToRun = $true)
. "$PSScriptRoot\common.ps1"

$projectDefinition = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
$version = ($projectDefinition.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (!$version) { throw 'Application version is missing.' }
$packageName = "Sticky-$version-win-x64-portable"
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot "dist\$packageName" }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$zipPath = $OutputDirectory + '.zip'
if ((Test-Path -LiteralPath $OutputDirectory) -or (Test-Path -LiteralPath $zipPath)) {
    throw 'Output already exists. Choose a new -OutputDirectory; existing portable Data folders are never overwritten.'
}
$cache = Join-Path $projectRoot '.tools\package-cache'
New-Item -ItemType Directory -Force -Path $cache | Out-Null

function Get-VerifiedDownload([string]$Name, [string]$Url, [string]$Sha256) {
    $destination = Join-Path $cache $Name
    if (!(Test-Path -LiteralPath $destination)) { Invoke-WebRequest -Uri $Url -OutFile $destination }
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha256) {
        throw "Hash mismatch: $Name. The file was not used."
    }
    return $destination
}

$gitUrl = 'https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.3/MinGit-2.56.0.3-64-bit.zip'
$gitSha = '2e5619966e588fde5f8122c5557cad50c0fbafcaed736689f40ec1dfb1d78ea7'
$gcmUrl = 'https://github.com/git-ecosystem/git-credential-manager/releases/download/v3.0.1/gcm-win-x64-3.0.1.zip'
$gcmSha = 'bc51abd9e613a856f909b5ea9244b5a265a4fc8a85383e2982d76034d80cfe3f'
$gitZip = Get-VerifiedDownload 'MinGit-2.56.0.3-64-bit.zip' $gitUrl $gitSha
$gcmZip = Get-VerifiedDownload 'gcm-win-x64-3.0.1.zip' $gcmUrl $gcmSha
$gcmLicense = Get-VerifiedDownload 'GCM-3.0.1-LICENSE.txt' 'https://raw.githubusercontent.com/git-ecosystem/git-credential-manager/v3.0.1/LICENSE' 'b47f1a8a744ecdc7a3da35804f88552805d33f51a726b87a2105acdfae406b07'

$publishDirectory = Join-Path $projectRoot ('.tools\publish-' + [Guid]::NewGuid().ToString('N'))
$publishArtifacts = Join-Path $projectRoot ('.tools\publish-build-' + [Guid]::NewGuid().ToString('N'))
$launcherDirectory = Join-Path $projectRoot ('.tools\launcher-publish-' + [Guid]::NewGuid().ToString('N'))
Push-Location $projectRoot
try {
    & $dotnet publish 'src\Sticky.App\Sticky.App.csproj' -c Release -r win-x64 --self-contained true `
        -p:PublishProfile=Portable -p:StickyPortable=true -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=$($ReadyToRun.IsPresent) `
        -m:1 -nr:false -p:UseSharedCompilation=false --artifacts-path $publishArtifacts -o $publishDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
    & $dotnet publish 'src\Sticky.Launcher\Sticky.Launcher.csproj' -c Release -r win-x64 --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false -p:PublishReadyToRun=$($ReadyToRun.IsPresent) -m:1 -nr:false -p:UseSharedCompilation=false `
        --artifacts-path $publishArtifacts -o $launcherDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Stable launcher publish failed.' }
} finally { Pop-Location }

New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $launcherDirectory 'Sticky.Launcher.exe') -Destination (Join-Path $OutputDirectory 'Sticky.exe')
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'tools\runtime') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDirectory 'Sticky.exe') -Destination (Join-Path $OutputDirectory 'tools\runtime\Sticky.exe')
Expand-Archive -LiteralPath $gitZip -DestinationPath (Join-Path $OutputDirectory 'tools\git')
Expand-Archive -LiteralPath $gcmZip -DestinationPath (Join-Path $OutputDirectory 'tools\gcm')
Copy-Item -LiteralPath $gcmLicense -Destination (Join-Path $OutputDirectory 'tools\gcm\LICENSE.txt')
$licenses = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $OutputDirectory 'tools\git\LICENSE.txt') -Destination (Join-Path $licenses 'Git-GPL-2.0.txt')
Copy-Item -LiteralPath $gcmLicense -Destination (Join-Path $licenses 'GCM-MIT.txt')
Copy-Item -LiteralPath (Join-Path $OutputDirectory 'tools\gcm\NOTICE') -Destination (Join-Path $licenses 'GCM-NOTICE.txt')
$nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:DOTNET_CLI_HOME '.nuget\packages' }
$runtimePackage = Join-Path $nugetRoot 'microsoft.netcore.app.runtime.win-x64'
$runtimeConfigs = @(Get-ChildItem -LiteralPath (Join-Path $publishArtifacts 'bin\Sticky.App') -Recurse -Filter 'Sticky.runtimeconfig.json')
if ($runtimeConfigs.Count -ne 1) { throw 'Expected exactly one runtime configuration from the isolated publish build.' }
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigs[0].FullName -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
if (!$runtimeVersion) { throw 'The publish output did not include a self-contained runtime.' }
Copy-Item -LiteralPath (Join-Path $runtimePackage "$runtimeVersion\LICENSE.TXT") -Destination (Join-Path $licenses 'DOTNET-MIT.txt')
Copy-Item -LiteralPath (Join-Path $runtimePackage "$runtimeVersion\THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $licenses 'DOTNET-THIRD-PARTY-NOTICES.txt')
$desktopLicense = Join-Path $nugetRoot "microsoft.windowsdesktop.app.runtime.win-x64\$runtimeVersion\LICENSE"
Copy-Item -LiteralPath $desktopLicense -Destination (Join-Path $licenses 'WPF-WINFORMS-LICENSE.txt')
$instructions = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging\使用说明.txt') -Raw -Encoding UTF8
[System.IO.File]::WriteAllText((Join-Path $OutputDirectory '使用说明.txt'), $instructions.Replace('{{VERSION}}', $version), [System.Text.UTF8Encoding]::new($true))
New-Item -ItemType Directory -Path (Join-Path $OutputDirectory 'assets') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Sticky.App\Assets\Sticky.svg') -Destination (Join-Path $OutputDirectory 'assets\Sticky.svg')
Copy-Item -LiteralPath (Join-Path $projectRoot 'packaging\THIRD-PARTY-SOURCES.txt') -Destination (Join-Path $licenses 'THIRD-PARTY-SOURCES.txt')
[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'portable.flag'), 'Sticky portable workspace; data is stored beside Sticky.exe in Data.')
$manifest = [ordered]@{
    application = 'Sticky'; version = $version; runtimeIdentifier = 'win-x64'; selfContained = $true; singleFile = $true
    dotnetRuntime = $runtimeVersion; dataDirectory = 'Data'; gitVersion = '2.56.0.windows.3'; gcmVersion = '3.0.1'; readyToRun = $ReadyToRun.IsPresent
    gitSource = $gitUrl; gitArchiveSha256 = $gitSha; gcmSource = $gcmUrl; gcmArchiveSha256 = $gcmSha
    executableSha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'Sticky.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    formatVersion = 2; launcherProtocol = 1; dataSchemaVersion = 1; runtimePath = 'tools/runtime/Sticky.exe'
    runtimeSha256 = (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'tools\runtime\Sticky.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
}
$programFiles = [ordered]@{}
Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($OutputDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
    $programFiles[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$manifest.files = $programFiles
[System.IO.File]::WriteAllText((Join-Path $OutputDirectory 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 4), [System.Text.UTF8Encoding]::new($false))
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'Data')) { throw 'A release must not contain personal data.' }
New-StickyPortableArchive -Directory $OutputDirectory -ZipPath $zipPath -PackageName $packageName
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText($zipPath + '.sha256', $zipHash + ' *' + [System.IO.Path]::GetFileName($zipPath) + [Environment]::NewLine)
Write-Output "Portable folder: $OutputDirectory"
Write-Output "Portable ZIP: $zipPath"
Write-Output "SHA256: $zipHash"
