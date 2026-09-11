#Requires -Version 5.1

<#
.SYNOPSIS
Verify client setup without installing software, launching games, or touching real user settings.
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Expect-Failure {
    param([scriptblock]$Action)
    $failed = $false
    try { & $Action *> $null } catch { $failed = $true }
    Assert $failed 'Invalid setup input must fail explicitly.'
}

foreach ($name in @('configure_client.ps1', 'build_client_release.ps1')) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $name), [ref]$tokens, [ref]$errors)
    Assert ($errors.Count -eq 0) "PowerShell parse failed: $name"
}

$fixture = Join-Path (Split-Path -Parent $PSScriptRoot) ('.client-setup-check ' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixture
$oldExitCode = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue | Select-Object Value
try {
    $client = Join-Path $fixture 'client with spaces'
    $null = New-Item -ItemType Directory -Path (Join-Path $client 'x64'), (Join-Path $client 'Data')
    foreach ($file in @('x64\MapleStory2.exe', 'x64\NxCharacter64.dll', 'Data\Xml.m2d', 'Data\Xml.m2h')) {
        [IO.File]::WriteAllText((Join-Path $client $file), 'test fixture, never executed')
    }
    $launcher = Join-Path $fixture 'Mushroom Launcher.exe'
    [IO.File]::WriteAllText($launcher, 'test fixture, never executed')
    $settings = Join-Path $fixture 'user data\app-config.json'
    $script = Join-Path $PSScriptRoot 'configure_client.ps1'
    $parameters = @{ ClientPath = $client; LauncherPath = $launcher; ConfigPath = $settings; NoShortcut = $true }
    & $script @parameters *> $null
    $created = Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($created.clientPath -eq $client) 'Client root was not saved correctly.'
    Assert ($created.servers.Count -eq 1 -and $created.servers[0].port -eq 20001) 'Local profile was not created.'
    Assert (-not $created.autoLogin -and -not $created.enableConsole) 'Unsafe launch defaults.'
    Assert ($null -eq $created.servers[0].PSObject.Properties['auth']) 'New setup must not invent or store credentials.'
    $id = $created.servers[0].id
    $created | Add-Member -NotePropertyName customSetting -NotePropertyValue 'retain'
    $savedPassword = 'synthetic-' + [char]0x00e9
    $created.servers[0] | Add-Member -NotePropertyName auth -NotePropertyValue @{ username = 'fixture'; password = $savedPassword }
    $created | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settings -Encoding UTF8
    & $script @parameters *> $null
    $updated = Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($updated.servers.Count -eq 1 -and $updated.servers[0].id -eq $id) 'Rerun duplicated or replaced the profile.'
    Assert ($updated.customSetting -eq 'retain' -and $updated.servers[0].auth.password -eq $savedPassword) 'Existing settings were lost.'

    & $script @parameters -LoginHost 'game.example.org' -LoginPort 25001 -ServerName 'Another server' *> $null
    $remote = Get-Content -LiteralPath $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    Assert ($remote.servers.Count -eq 2 -and $remote.servers[1].ip -eq 'game.example.org') 'Remote profile was not merged.'
    Assert ($remote.servers[0].auth.password -eq $savedPassword) 'UTF-8 settings without a BOM were corrupted.'
    $before = (Get-FileHash -LiteralPath $settings).Hash
    Expect-Failure { & $script @parameters -LoginHost 'https://game.example.org' }
    Expect-Failure { & $script @parameters -LoginPort 0 }
    $wrongRoot = $parameters.Clone()
    $wrongRoot.ClientPath = Join-Path $client 'x64'
    Expect-Failure { & $script @wrongRoot }
    Assert ((Get-FileHash -LiteralPath $settings).Hash -eq $before) 'Failed validation changed existing settings.'

    [IO.File]::WriteAllText((Join-Path $client 'x64\AgarciumClient.dll'), 'fixture')
    Expect-Failure { & $script @parameters }
    Assert ((Get-FileHash -LiteralPath $settings).Hash -eq $before) 'Missing original DLL backup changed settings.'
    [IO.File]::WriteAllText((Join-Path $client 'x64\NxCharacter64.dll.bak'), 'original fixture')
    & $script @parameters *> $null
    Remove-Item -LiteralPath $launcher
    Expect-Failure { & $script @parameters }
    [IO.File]::WriteAllText($launcher, 'fixture')
    [IO.File]::WriteAllText($settings, 'not valid JSON')
    Expect-Failure { & $script @parameters }
    Assert ((Get-Content -LiteralPath $settings -Raw) -eq 'not valid JSON') 'Corrupt settings were silently replaced.'
    Assert (-not (Test-Path -LiteralPath (Join-Path $client 'Mushroom Launcher.lnk'))) 'NoShortcut created a shortcut.'
    Assert (@(Get-ChildItem -LiteralPath (Split-Path -Parent $settings) -File -Filter '*.tmp').Count -eq 0) 'Temporary settings files remain.'

    $source = Join-Path $fixture 'source'
    $sourceScripts = Join-Path $source 'scripts'
    $output = Join-Path $fixture 'release'
    $null = New-Item -ItemType Directory -Path $sourceScripts
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'build_client_release.ps1') -Destination $sourceScripts
    foreach ($file in @('scripts\configure_client.ps1', 'CLIENT_SETUP.md', 'LICENSE', '.env', 'MapleStory2.exe')) {
        [IO.File]::WriteAllText((Join-Path $source $file), 'fixture, not for execution')
    }
    $gitState = @{ Dirty = $false }
    function Invoke-TestGit {
        param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
        $global:LASTEXITCODE = 0
        if ($Arguments -contains 'rev-parse') { return ('a' * 40) }
        if ($Arguments -contains 'status') {
            if ($gitState.Dirty) { ' M CLIENT_SETUP.md' }
            return
        }
        throw 'Unexpected Git command during release-kit verification.'
    }
    Set-Alias -Name git -Value Invoke-TestGit -Scope Local
    $builder = Join-Path $sourceScripts 'build_client_release.ps1'
    & $builder -OutputDirectory $output *> $null
    $zip = Join-Path $output 'Maple2-Client-Setup-aaaaaaaa.zip'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $package = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $names = @($package.Entries | Where-Object Name | ForEach-Object Name | Sort-Object)
        Assert (($names -join ',') -eq 'Configure-Client.ps1,LICENSE,README.md,SOURCE.txt') 'Release allowlist includes extra files or omitted a required file.'
    } finally {
        $package.Dispose()
    }
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    Assert ((Get-Content -LiteralPath "$zip.sha256" -Raw).StartsWith($hash, [StringComparison]::OrdinalIgnoreCase)) 'Release checksum does not match the ZIP.'
    Expect-Failure { & $builder -OutputDirectory $output }
    Assert ((Get-FileHash -LiteralPath $zip).Hash -eq $hash) 'Rerun overwrote the release.'
    $gitState.Dirty = $true
    $dirtyOutput = Join-Path $fixture 'dirty output'
    Expect-Failure { & $builder -OutputDirectory $dirtyOutput }
    Assert (-not (Test-Path -LiteralPath $dirtyOutput)) 'A dirty source checkout produced a release.'
    Write-Host 'Client setup and release checks passed; no launcher, game, account, or real settings were used.'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
    if ($oldExitCode) {
        $global:LASTEXITCODE = $oldExitCode.Value
    } else {
        Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
}
