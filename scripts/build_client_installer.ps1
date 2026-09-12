#Requires -Version 5.1
<#
.SYNOPSIS
Build the MS2 per-user bootstrap installer, not a proprietary game payload.
.PARAMETER Preview
Build an explicitly labeled local preview from uncommitted source.
Normal release builds require a clean checkout. Neither mode commits or publishes.
#>
param(
    [Parameter(Mandatory = $true)][string]$InnoCompiler,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'release'),
    [switch]$Preview
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = (Resolve-Path -LiteralPath $InnoCompiler).ProviderPath
$compilerVersionText = (& $compiler --version | Out-String).Trim()
$compilerVersion = $null
if ($LASTEXITCODE -ne 0 -or
    -not [version]::TryParse($compilerVersionText, [ref]$compilerVersion) -or
    $compilerVersion.Major -lt 7) {
    throw 'Inno Setup 7 or newer is required; the compiler must report its version with --version.'
}
$revision = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[a-f0-9]{40}$') { throw 'A Git source revision is required.' }
$changes = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect source state.' }
if ($changes.Count -gt 0 -and -not $Preview) {
    throw 'The checkout is not clean. Commit and validate a release, or explicitly request -Preview.'
}
$distribution = Get-Content -LiteralPath (Join-Path $root 'installer\distribution.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($distribution.version -notmatch '^\d+\.\d+\.\d+$' -or
    [Uri]::CheckHostName($distribution.loginHost) -notin @([UriHostNameType]::IPv4, [UriHostNameType]::Dns) -or
    $distribution.loginPort -lt 1 -or $distribution.loginPort -gt 65535 -or
    $distribution.serverName -match '[\r\n"]') {
    throw 'Installer version or Login endpoint is invalid.'
}
foreach ($value in @($distribution.registrationUrl, $distribution.websiteUrl,
    $distribution.mushroom.url, $distribution.vcRuntime.url)) {
    $uri = $null
    if (-not [Uri]::TryCreate($value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or $uri.UserInfo -or $value -match '[\r\n"]') {
        throw 'Installer URLs must be HTTPS without embedded credentials.'
    }
}
foreach ($hash in @($distribution.clientExeSha256, $distribution.mushroom.sha256, $distribution.vcRuntime.sha256)) {
    if ($hash -notmatch '^[a-f0-9]{64}$') { throw 'Every installer/client download must have a pinned SHA-256.' }
}
$runtime = [version]$distribution.vcRuntime.minimumVersion
foreach ($part in @($runtime.Major, $runtime.Minor, $runtime.Build, $runtime.Revision)) {
    if ($part -lt 0 -or $part -gt 65535) { throw 'The runtime version must have four unsigned 16-bit components.' }
}
foreach ($asset in $distribution.artwork.PSObject.Properties) {
    $path = Join-Path (Join-Path $root 'website') $asset.Name
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $asset.Value) {
        throw "Installer artwork differs from its reviewed source: $($asset.Name)"
    }
}
$sources = @(
    'installer\MapleTimeMS2.iss', 'installer\distribution.json', 'installer\README.txt',
    'installer\render_art.py', 'installer\requirements.txt', 'scripts\build_client_installer.ps1',
    'scripts\configure_client.ps1', 'scripts\test_client_installer.ps1', 'CLIENT_SETUP.md', 'LICENSE',
    'website\ms2-logo.png', 'website\ms2-world-mobile.webp', 'website\ms2-mushroom.webp'
)
$hashes = [ordered]@{}
foreach ($file in ($sources | Sort-Object)) {
    $hashes[$file] = (Get-FileHash -LiteralPath (Join-Path $root $file) -Algorithm SHA256).Hash.ToLowerInvariant()
}
$hashInput = [Text.Encoding]::UTF8.GetBytes(($hashes | ConvertTo-Json -Compress))
$sha = [Security.Cryptography.SHA256]::Create()
try { $sourceHash = -join ($sha.ComputeHash($hashInput) | ForEach-Object { $_.ToString('x2') }) }
finally { $sha.Dispose() }
$kind = if ($Preview) { 'preview' } else { 'release' }
$name = "MapleTime-MS2-$($distribution.version)-$kind-$($sourceHash.Substring(0, 12))-Setup"
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$output = Join-Path $OutputDirectory "$name.exe"
$sourceOutput = Join-Path $OutputDirectory "$name-source.zip"
foreach ($artifact in @($output, "$output.sha256", "$output.json", $sourceOutput)) {
    if (Test-Path -LiteralPath $artifact) { throw 'An artifact for this build already exists; refusing to overwrite it.' }
}
$stage = Join-Path ([IO.Path]::GetTempPath()) ('MapleTimeMS2Build-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
$source = Join-Path $stage 'source'
$artwork = Join-Path $stage 'artwork'
$null = New-Item -ItemType Directory -Path $payload, $source, $artwork, $OutputDirectory -Force
try {
    foreach ($file in $sources) {
        $destination = Join-Path $source $file
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination
    }
    $buildInfo = [ordered]@{
        version = $distribution.version
        kind = $kind
        gitBase = $revision
        uncommittedSource = $changes.Count -gt 0
        sourceSha256 = $sourceHash
        sourceFiles = $hashes
        compilerVersion = $compilerVersion.ToString()
        signed = $false
        includesGameFiles = $false
    }
    [IO.File]::WriteAllText((Join-Path $source 'BUILD.json'), ($buildInfo | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Compress-Archive -Path (Join-Path $source '*') -DestinationPath (Join-Path $payload 'SOURCE.zip')
    Copy-Item -LiteralPath (Join-Path $root 'scripts\configure_client.ps1') -Destination (Join-Path $payload 'Configure-Client.ps1')
    Copy-Item -LiteralPath (Join-Path $root 'installer\README.txt') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $root 'CLIENT_SETUP.md'), (Join-Path $root 'LICENSE') -Destination $payload
    $sourceText = "MapleTime MS2 $kind build`r`nGit base: $revision`r`nSource fingerprint: $sourceHash`r`nUncommitted source: $($buildInfo.uncommittedSource)`r`n`r`nExact authored source is in SOURCE.zip. To rebuild, clone the Git base, overlay`r`nthe archive, install installer\requirements.txt, and run the included builder.`r`nGame files and upstream installers are not included. Artwork copyright NEXON.`r`n"
    [IO.File]::WriteAllText((Join-Path $payload 'SOURCE.txt'), $sourceText, [Text.UTF8Encoding]::new($false))
    & python (Join-Path $root 'installer\render_art.py') (Join-Path $root 'website') $artwork
    if ($LASTEXITCODE -ne 0) { throw 'Installer artwork generation failed; install installer\requirements.txt if Pillow is missing.' }
    $defines = [ordered]@{
        AppId = if ($Preview) { '{{d6f71c49-5cf4-4640-a6a5-8f9ef23872b7}' } else { '{{e581b8c9-9803-4e3f-89b9-714c7026df42}' }
        ProductName = if ($Preview) { 'MapleTime MS2 Preview' } else { 'MapleTime MS2' }
        AppVersion = $distribution.version
        BuildLabel = "$($distribution.version) $kind"
        OutputDirectory = $OutputDirectory
        OutputBaseFilename = $name
        PayloadDirectory = $payload
        ArtworkDirectory = $artwork
        LauncherPath = '{localappdata}\mushroom_launcher\Mushroom Launcher.exe'
        LauncherConfigPath = '{userappdata}\Mushroom Launcher\app-config.json'
        RuntimeDirectory = '{sys}'
        ClientExeSha256 = $distribution.clientExeSha256
        LoginHost = $distribution.loginHost
        LoginPort = [string]$distribution.loginPort
        ServerName = $distribution.serverName
        RegistrationUrl = $distribution.registrationUrl
        RuntimeUrl = $distribution.vcRuntime.url
        RuntimeSha256 = $distribution.vcRuntime.sha256
        RuntimeVersionHigh = [string](([uint64]$runtime.Major -shl 16) -bor [uint64]$runtime.Minor)
        RuntimeVersionLow = [string](([uint64]$runtime.Build -shl 16) -bor [uint64]$runtime.Revision)
        MushroomUrl = $distribution.mushroom.url
        MushroomSha256 = $distribution.mushroom.sha256
    }
    $lines = foreach ($entry in $defines.GetEnumerator()) {
        '#define ' + $entry.Key + ' "' + ([string]$entry.Value).Replace('"', '""') + '"'
    }
    [IO.File]::WriteAllLines((Join-Path $stage 'build-defines.iss'), [string[]]$lines, [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $root 'installer\MapleTimeMS2.iss') -Destination $stage
    & $compiler --quiet (Join-Path $stage 'MapleTimeMS2.iss')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $output)) { throw 'Installer compilation failed.' }
    $buildInfo.installerSha256 = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$output.sha256", "$($buildInfo.installerSha256)  $name.exe`r`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText("$output.json", ($buildInfo | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $payload 'SOURCE.zip') -Destination $sourceOutput
    Write-Host "Installer built: $output"
    Write-Host 'Unsigned private-pilot bootstrap only; game files, credentials and upstream installers are not bundled.'
    Write-Host 'Compilation is not release approval. Check the antivirus and clean-PC gates in CLIENT_SETUP.md.'
} finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
