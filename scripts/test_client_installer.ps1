#Requires -Version 5.1
param([string]$InnoCompiler, [string]$InstallerPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
function Assert([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Expect-Failure([scriptblock]$Action) {
    $failed = $false
    try { & $Action *> $null } catch { $failed = $true }
    Assert $failed 'Invalid installer build input must fail explicitly.'
}
function Assert-Shortcut([string]$Path, [string]$Target, [string]$WorkingDirectory) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($Path)
        try {
            Assert ($shortcut.TargetPath -eq $Target -and $shortcut.WorkingDirectory -eq $WorkingDirectory) 'Shortcut has the wrong target or working directory.'
        } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
$sourceFiles = @(
    'installer\MapleTimeMS2.iss', 'installer\distribution.json', 'installer\README.txt',
    'installer\render_art.py', 'installer\requirements.txt', 'scripts\build_client_installer.ps1',
    'scripts\configure_client.ps1', 'scripts\test_client_installer.ps1', 'CLIENT_SETUP.md', 'LICENSE',
    'website\ms2-logo.png', 'website\ms2-world-mobile.webp', 'website\ms2-mushroom.webp'
)
function Test-Artifact([string]$Path) {
    $info = Get-Content -LiteralPath "$Path.json" -Raw -Encoding UTF8 | ConvertFrom-Json
    $hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert ($hash -eq $info.installerSha256) 'Installer differs from its build manifest.'
    $checksum = (Get-Content -LiteralPath "$Path.sha256" -Raw).TrimEnd()
    Assert ($checksum -eq "$hash  $([IO.Path]::GetFileName($Path))") 'Installer checksum sidecar is incorrect.'
    Assert (([version]$info.compilerVersion).Major -ge 7) 'Compiler CLI version was not recorded.'
    Assert (-not $info.signed -and -not $info.includesGameFiles) 'Incorrect distribution claims.'
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($info.sourceFiles | ConvertTo-Json -Compress))
        $fingerprint = -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    } finally { $sha.Dispose() }
    Assert ($fingerprint -eq $info.sourceSha256) 'Source fingerprint does not match the manifest contents.'
    $expected = @($sourceFiles + 'BUILD.json' | Sort-Object)
    $sourceZip = Join-Path (Split-Path -Parent $Path) ([IO.Path]::GetFileNameWithoutExtension($Path) + '-source.zip')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($sourceZip)
    try {
        $files = @($archive.Entries | Where-Object Name)
        $names = @($files | ForEach-Object { $_.FullName.Replace('/', '\') } | Sort-Object)
        Assert (($names -join '|') -eq ($expected -join '|')) 'Source archive omitted required files or included private/game payloads.'
        $manifestNames = @($info.sourceFiles.PSObject.Properties.Name | Sort-Object)
        Assert (($manifestNames -join '|') -eq (($sourceFiles | Sort-Object) -join '|')) 'Source manifest does not match the allowlist.'
        foreach ($entry in $files) {
            $stream = $entry.Open()
            try {
                if ($entry.Name -eq 'BUILD.json') {
                    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
                    try { $embedded = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
                    Assert ($embedded.sourceSha256 -eq $info.sourceSha256 -and
                        $embedded.compilerVersion -eq $info.compilerVersion) 'Embedded source manifest does not match this build.'
                } else {
                    $sha = [Security.Cryptography.SHA256]::Create()
                    try { $fileHash = -join ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) }
                    finally { $sha.Dispose() }
                    Assert ($fileHash -eq $info.sourceFiles.PSObject.Properties[$entry.FullName.Replace('/', '\')].Value) 'Archived source differs from its manifest.'
                }
            } finally { $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
}
function Test-Builder {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('MapleTimeMS2BuildCheck-' + [Guid]::NewGuid().ToString('N'))
    $source = Join-Path $fixture 'source'
    $output = Join-Path $fixture 'output'
    $compiler = Join-Path $fixture 'compiler.ps1'
    $oldExitCode = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue | Select-Object Value
    $gitState = @{ Dirty = $true }
    function Invoke-TestGit {
        param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
        $global:LASTEXITCODE = 0
        if ($Arguments -contains 'rev-parse') { return ('a' * 40) }
        if ($Arguments -contains 'status') {
            if ($gitState.Dirty) { ' M CLIENT_SETUP.md' }
            return
        }
        throw 'Unexpected Git command during installer build checks.'
    }
    function Invoke-TestPython { $global:LASTEXITCODE = 0 }
    Set-Alias -Name git -Value Invoke-TestGit -Scope Local
    Set-Alias -Name python -Value Invoke-TestPython -Scope Local
    try {
        foreach ($file in $sourceFiles) {
            $destination = Join-Path $source $file
            $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
            Copy-Item -LiteralPath (Join-Path $root $file) -Destination $destination
        }
        [IO.File]::WriteAllText((Join-Path $source '.env'), 'synthetic private-file exclusion check')
        [IO.File]::WriteAllText((Join-Path $source 'MapleStory2.exe'), 'inert excluded file; never executed')
        [IO.File]::WriteAllText((Join-Path $fixture 'compiler-version.txt'), '7.1.0')
        [IO.File]::WriteAllText($compiler, @'
param([string]$Flag, [string]$Definition)
$global:LASTEXITCODE = 0
if ($Flag -eq '--version') {
    Get-Content -LiteralPath (Join-Path $PSScriptRoot 'compiler-version.txt')
    return
}
if ($Flag -ne '--quiet') { throw 'Unexpected compiler invocation.' }
$defines = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path (Split-Path -Parent $Definition) 'build-defines.iss')) {
    if ($line -notmatch '^#define (\w+) "(.*)"$') { throw 'Invalid compiler definition.' }
    $defines[$matches[1]] = $matches[2].Replace('""', '"')
}
if ($defines.RuntimeVersionHigh -ne '917548' -or $defines.RuntimeVersionLow -ne '2307588096') {
    throw 'Runtime DWORDs must retain all unsigned version bits.'
}
$path = Join-Path $defines.OutputDirectory ($defines.OutputBaseFilename + '.exe')
[IO.File]::WriteAllText($path, 'inert compiler output; not an executable and never launched')
'@)
        $builder = Join-Path $source 'scripts\build_client_installer.ps1'
        Expect-Failure { & $builder -InnoCompiler $compiler -OutputDirectory $output }
        Assert (-not (Test-Path -LiteralPath $output)) 'A dirty checkout produced an unlabeled release.'
        & $builder -InnoCompiler $compiler -OutputDirectory $output -Preview *> $null
        $artifact = @(Get-ChildItem -LiteralPath $output -File -Filter '*.exe')
        Assert ($artifact.Count -eq 1) 'Preview did not produce exactly one artifact.'
        Test-Artifact $artifact[0].FullName
        $info = Get-Content -LiteralPath ($artifact[0].FullName + '.json') -Raw | ConvertFrom-Json
        Assert ($info.kind -eq 'preview' -and $info.uncommittedSource -and
            $info.compilerVersion -eq '7.1.0') 'Preview provenance or CLI version is incorrect.'
        Expect-Failure { & $builder -InnoCompiler $compiler -OutputDirectory $output -Preview }
        Remove-Item -LiteralPath $artifact[0].FullName
        Expect-Failure { & $builder -InnoCompiler $compiler -OutputDirectory $output -Preview }
        Assert (-not (Test-Path -LiteralPath $artifact[0].FullName)) 'A surviving sidecar was overwritten after the EXE was removed.'
        $gitState.Dirty = $false
        $cleanOutput = Join-Path $fixture 'clean output'
        & $builder -InnoCompiler $compiler -OutputDirectory $cleanOutput *> $null
        $artifact = Get-ChildItem -LiteralPath $cleanOutput -File -Filter '*.exe'
        Test-Artifact $artifact.FullName
        $info = Get-Content -LiteralPath ($artifact.FullName + '.json') -Raw | ConvertFrom-Json
        Assert ($info.kind -eq 'release' -and -not $info.uncommittedSource) 'Clean release provenance is incorrect.'
        foreach ($version in @('6.9.0', 'not a version')) {
            [IO.File]::WriteAllText((Join-Path $fixture 'compiler-version.txt'), $version)
            Expect-Failure { & $builder -InnoCompiler $compiler -OutputDirectory (Join-Path $fixture 'invalid compiler') -Preview }
        }
    } finally {
        if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Recurse -Force }
        if ($oldExitCode) { $global:LASTEXITCODE = $oldExitCode.Value }
        else { Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue }
    }
}
foreach ($file in @('configure_client.ps1', 'build_client_installer.ps1')) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot $file), [ref]$tokens, [ref]$errors)
    Assert ($errors.Count -eq 0) "Installer script does not parse: $file"
}
$definition = Get-Content -LiteralPath (Join-Path $root 'installer\MapleTimeMS2.iss') -Raw
$distribution = Get-Content -LiteralPath (Join-Path $root 'installer\distribution.json') -Raw | ConvertFrom-Json
Assert ($definition -match 'PrivilegesRequired=lowest') 'Setup must be per-user.'
Assert ($definition -notmatch 'RUNASADMIN|\[InstallDelete\]|\[UninstallDelete\]') 'Setup must not elevate the game or remove client files.'
Assert ($definition -match '-NoShortcut' -and $definition -match '-ValidateOnly') 'Setup must reuse safe configuration preflight.'
Assert ($definition -match 'MushroomSha256' -and $definition -match 'RuntimeSha256') 'Publisher downloads must be hash-verified.'
Assert ($definition -match 'Source:.*SOURCE.zip') 'Exact authored setup source must accompany the installer.'
Assert ($definition -match "if NeedsRestart then begin\s+Result := '.+';\s+exit;") 'Runtime reboot requests must stop setup before client configuration.'
Assert ($distribution.loginPort -eq 20001 -and $distribution.registrationUrl -like 'https://*') 'Wrong pilot endpoint contract.'
Assert ($distribution.mushroom.url -like 'https://github.com/shuabritze/mushroom-launcher/releases/download/*') 'Mushroom must come directly from its author.'
Test-Builder
if ($InstallerPath) {
    Test-Artifact (Resolve-Path -LiteralPath $InstallerPath).ProviderPath
    Write-Host 'Installer artifact, checksum and exact-source archive checks passed; no executable was run.'
}
if (-not $InnoCompiler) {
    Write-Host 'Installer source/build contracts passed. Native install/uninstall checks require -InnoCompiler.'
    return
}
$compiler = (Resolve-Path -LiteralPath $InnoCompiler).ProviderPath
$fixtureId = [Guid]::NewGuid().ToString('N')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('MapleTimeMS2Smoke-' + $fixtureId)
$productName = 'MapleTime MS2 Fixture ' + $fixtureId.Substring(0, 8)
$client = Join-Path $fixture "client's files"
$payload = Join-Path $fixture 'payload'
$artwork = Join-Path $fixture 'artwork'
$runtime = Join-Path $fixture 'runtime'
$settings = Join-Path $fixture 'launcher data\app-config.json'
$launcher = Join-Path $fixture 'launcher\Mushroom Launcher.exe'
$app = Join-Path $fixture 'installed helper'
$setup = Join-Path $fixture 'SmokeSetup.exe'
$menuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$productName\$productName.lnk"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) "$productName.lnk"
$null = New-Item -ItemType Directory -Path (Join-Path $client 'x64'), (Join-Path $client 'Data'),
    $payload, $artwork, $runtime, (Split-Path -Parent $settings), (Split-Path -Parent $launcher) -Force
$completed = $false
try {
    foreach ($file in @('x64\MapleStory2.exe', 'x64\NxCharacter64.dll', 'Data\Xml.m2d', 'Data\Xml.m2h')) {
        [IO.File]::WriteAllText((Join-Path $client $file), 'inert installer fixture; never executed')
    }
    [IO.File]::WriteAllText($launcher, 'inert launcher fixture; never executed')
    $clientHashes = @(Get-ChildItem -LiteralPath $client -Recurse -File | Get-FileHash | ForEach-Object { $_.Path + '|' + $_.Hash })
    foreach ($name in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')) {
        Copy-Item -LiteralPath $compiler -Destination (Join-Path $runtime $name)
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'configure_client.ps1') -Destination (Join-Path $payload 'Configure-Client.ps1')
    foreach ($name in @('README.txt', 'CLIENT_SETUP.md', 'LICENSE', 'SOURCE.txt', 'SOURCE.zip')) {
        [IO.File]::WriteAllText((Join-Path $payload $name), 'Inert fixture documentation.')
    }
    & python (Join-Path $root 'installer\render_art.py') (Join-Path $root 'website') $artwork
    Assert ($LASTEXITCODE -eq 0) 'Fixture artwork generation failed.'
    $defines = [ordered]@{
        AppId = '{{' + [Guid]::NewGuid().ToString() + '}'
        ProductName = $productName
        AppVersion = '0.0.1'
        BuildLabel = 'isolated smoke test'
        OutputDirectory = $fixture
        OutputBaseFilename = 'SmokeSetup'
        PayloadDirectory = $payload
        ArtworkDirectory = $artwork
        LauncherPath = $launcher
        LauncherConfigPath = $settings
        RuntimeDirectory = $runtime
        RuntimeVersionHigh = '0'
        RuntimeVersionLow = '0'
        ClientExeSha256 = (Get-FileHash -LiteralPath (Join-Path $client 'x64\MapleStory2.exe')).Hash.ToLowerInvariant()
        LoginHost = '192.0.2.10'
        LoginPort = '20001'
        ServerName = 'Fixture Azure'
        RegistrationUrl = 'https://example.invalid/account'
        RuntimeUrl = 'https://example.invalid/runtime.exe'
        RuntimeSha256 = 'a' * 64
        MushroomUrl = 'https://example.invalid/mushroom.exe'
        MushroomSha256 = 'b' * 64
    }
    $lines = foreach ($entry in $defines.GetEnumerator()) {
        '#define ' + $entry.Key + ' "' + ([string]$entry.Value).Replace('"', '""') + '"'
    }
    [IO.File]::WriteAllLines((Join-Path $fixture 'build-defines.iss'), [string[]]$lines, [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $root 'installer\MapleTimeMS2.iss') -Destination $fixture
    & $compiler --quiet (Join-Path $fixture 'MapleTimeMS2.iss')
    Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $setup)) 'Fixture installer did not compile.'
    $scanner = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    Assert (Test-Path -LiteralPath $scanner) 'Native fixtures require Windows Defender for a pre-execution scan.'
    $scan = @(& $scanner -Scan -ScanType 3 -File $setup 2>&1 | ForEach-Object { [string]$_ })
    Assert ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $setup) -and
        ($scan -join ' ') -match 'found no threats\.') "Native fixture scan was not cleared; no setup will be run. $($scan -join ' ')"
    function Run-Setup([string]$SelectedClient, [string]$Destination = $app, [switch]$DesktopIcon) {
        $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$Destination`"",
            "/CLIENTDIR=`"$SelectedClient`"", "/LOG=`"$(Join-Path $fixture 'setup.log')`"")
        if ($DesktopIcon) { $arguments += '/TASKS=desktopicon' }
        $process = Start-Process -FilePath $setup -ArgumentList $arguments -Wait -PassThru
        return $process.ExitCode
    }
    Assert ((Run-Setup (Join-Path $client 'x64')) -ne 0) 'Wrong client root was accepted.'
    Assert (-not (Test-Path -LiteralPath $settings)) 'Rejected setup created launcher settings.'
    $clientExe = Join-Path $client 'x64\MapleStory2.exe'
    $original = [IO.File]::ReadAllBytes($clientExe)
    [IO.File]::WriteAllText($clientExe, 'incompatible inert executable')
    Assert ((Run-Setup $client) -ne 0) 'An incompatible client executable was accepted.'
    Assert (-not (Test-Path -LiteralPath $settings)) 'Rejected client version created launcher settings.'
    [IO.File]::WriteAllBytes($clientExe, $original)
    foreach ($destination in @($client, (Join-Path $client 'helper'), $fixture,
        (Split-Path -Parent $launcher), (Join-Path (Split-Path -Parent $launcher) 'helper'))) {
        Assert ((Run-Setup $client $destination) -ne 0) 'A helper directory overlapping client or launcher files was accepted.'
    }
    $null = New-Item -ItemType Directory -Path $app
    [IO.File]::WriteAllText((Join-Path $app 'keep.txt'), 'unrelated file')
    Assert ((Run-Setup $client) -ne 0) 'A nonempty, unrelated helper folder was accepted.'
    Assert ((Get-Content -LiteralPath (Join-Path $app 'keep.txt') -Raw) -eq 'unrelated file') 'Setup modified an unrelated file.'
    Remove-Item -LiteralPath (Join-Path $app 'keep.txt')
    Remove-Item -LiteralPath $app
    [IO.File]::WriteAllText($settings, 'invalid settings')
    Assert ((Run-Setup $client) -ne 0) 'Malformed launcher settings were accepted.'
    Assert ((Get-Content -LiteralPath $settings -Raw) -eq 'invalid settings') 'Failed setup changed user data.'
    $existing = [pscustomobject]@{
        custom = 'preserve'
        servers = @([pscustomobject]@{
            id = 'existing'; name = 'Local'; ip = '127.0.0.1'; port = 20001
            auth = [pscustomobject]@{ username = 'fixture'; password = 'synthetic-' + [char]0x00e9 }
        })
    }
    [IO.File]::WriteAllText($settings, ($existing | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $before = Get-FileHash -LiteralPath (Join-Path $client 'x64\MapleStory2.exe')
    Assert ((Run-Setup $client) -eq 0) 'Valid fixture installation failed.'
    $configured = Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($configured.clientPath -eq $client -and $configured.servers.Count -eq 2) 'Client/profile configuration is incomplete.'
    Assert ($configured.custom -eq 'preserve' -and
        $configured.servers[0].auth.password -eq $existing.servers[0].auth.password) 'Existing user settings were not preserved.'
    Assert (-not $configured.autoLogin -and -not $configured.enableConsole) 'Unsafe launcher defaults.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $client 'Mushroom Launcher.lnk'))) 'Installer wrote into the client directory.'
    Assert (Test-Path -LiteralPath $menuShortcut) 'The Start-menu shortcut was not installed.'
    Assert-Shortcut $menuShortcut $launcher $client
    Assert (-not (Test-Path -LiteralPath $desktopShortcut)) 'Setup created a desktop shortcut without selecting the optional task.'
    $remoteId = $configured.servers[1].id
    Assert ((Run-Setup $client -DesktopIcon) -eq 0) 'Idempotent installer rerun failed.'
    $configured = Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($configured.servers.Count -eq 2 -and $configured.servers[1].id -eq $remoteId) 'Reinstall duplicated the server profile.'
    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash
    Assert (Test-Path -LiteralPath $desktopShortcut) 'The selected desktop shortcut task was not installed.'
    Assert-Shortcut $desktopShortcut $launcher $client
    Move-Item -LiteralPath $launcher -Destination "$launcher.fixture-backup"
    try {
        Assert ((Run-Setup $client) -ne 0) 'A failed publisher prerequisite download was ignored.'
        Assert ((Get-FileHash -LiteralPath $settings).Hash -eq $settingsHash) 'Prerequisite failure changed existing profiles.'
    } finally { Move-Item -LiteralPath "$launcher.fixture-backup" -Destination $launcher }
    $uninstall = Join-Path $app 'unins000.exe'
    $process = Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES',
        '/NORESTART', "/LOG=`"$(Join-Path $fixture 'uninstall.log')`"" -Wait -PassThru
    Assert ($process.ExitCode -eq 0) 'Fixture uninstallation failed.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $app 'Configure-Client.ps1'))) 'Uninstall left its helper installed.'
    Assert ((Get-FileHash -LiteralPath $settings).Hash -eq $settingsHash) 'Uninstall changed launcher profiles.'
    Assert ((Get-FileHash -LiteralPath (Join-Path $client 'x64\MapleStory2.exe')).Hash -eq $before.Hash) 'Setup changed the client executable.'
    $afterHashes = @(Get-ChildItem -LiteralPath $client -Recurse -File | Get-FileHash | ForEach-Object { $_.Path + '|' + $_.Hash })
    Assert (($afterHashes -join '|') -eq ($clientHashes -join '|')) 'Setup or uninstall changed client files.'
    Assert (Test-Path -LiteralPath $launcher) 'Uninstall removed the shared launcher.'
    Assert (-not (Test-Path -LiteralPath $menuShortcut) -and
        -not (Test-Path -LiteralPath $desktopShortcut)) 'Uninstall left fixture shortcuts behind.'
    $completed = $true
} finally {
    $uninstall = Join-Path $app 'unins000.exe'
    if (Test-Path -LiteralPath $uninstall) {
        $process = Start-Process -FilePath $uninstall -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES',
            '/NORESTART', "/LOG=`"$(Join-Path $fixture 'cleanup-uninstall.log')`"" -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Fixture cleanup failed; retained $fixture for inspection." }
    }
    if ($completed) {
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            try {
                Remove-Item -LiteralPath $fixture -Recurse -Force
                break
            } catch [IO.IOException] {
                if ($attempt -eq 19) { throw }
                Start-Sleep -Milliseconds 250
            }
        }
    } else {
        Write-Warning "Failed native fixture and logs retained for inspection: $fixture"
    }
}
Write-Host 'Compiled installer rejection, install, reinstall and uninstall checks passed using inert fixtures only.'
