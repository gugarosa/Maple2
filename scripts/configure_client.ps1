#Requires -Version 5.1

<#
.SYNOPSIS
Configure official Mushroom Launcher for an existing, user-supplied x64 client.
.DESCRIPTION
Install Mushroom Launcher first and close it before running this script.
Preserves existing server entries, credentials, mods, and game files. New profiles
have no saved credentials. Never downloads, patches, or launches the game.
.PARAMETER ClientPath
Client root containing Data and x64, not the x64 directory itself.
.PARAMETER LoginHost
Login server IPv4 address or DNS name, not a URL or a game-channel address.
.PARAMETER NoShortcut
Update settings without creating the client-local Windows shortcut.
#>
param(
    [string]$ClientPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'client'),
    [string]$LoginHost = '127.0.0.1',
    [ValidateRange(1, 65535)][int]$LoginPort = 20001,
    [ValidateNotNullOrEmpty()][string]$ServerName = 'Maple2 Local',
    [string]$LauncherPath = (Join-Path $env:LOCALAPPDATA 'mushroom_launcher\Mushroom Launcher.exe'),
    [string]$ConfigPath = (Join-Path $env:APPDATA 'Mushroom Launcher\app-config.json'),
    [switch]$NoShortcut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'Mushroom client setup requires Windows.'
}
if ([Uri]::CheckHostName($LoginHost) -notin @([UriHostNameType]::IPv4, [UriHostNameType]::Dns)) {
    throw 'LoginHost must be an IPv4 address or DNS name, without a URL scheme or port.'
}
if (Get-Process -Name 'Mushroom Launcher' -ErrorAction SilentlyContinue) {
    throw 'Close Mushroom Launcher before changing its settings; an open window can overwrite them.'
}
$ClientPath = (Resolve-Path -LiteralPath $ClientPath).ProviderPath
foreach ($file in @('x64\MapleStory2.exe', 'x64\NxCharacter64.dll', 'Data\Xml.m2d', 'Data\Xml.m2h')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ClientPath $file) -PathType Leaf)) {
        throw "Missing client asset: $file. Select the original client root containing Data and x64."
    }
}
if ((Test-Path -LiteralPath (Join-Path $ClientPath 'x64\AgarciumClient.dll')) -and
    -not (Test-Path -LiteralPath (Join-Path $ClientPath 'x64\NxCharacter64.dll.bak') -PathType Leaf)) {
    throw 'Patched client is missing its genuine NxCharacter64.dll.bak. Restore the original DLL; never copy the proxy as its backup.'
}
if (-not (Test-Path -LiteralPath $LauncherPath -PathType Leaf)) {
    throw 'Mushroom Launcher is not installed. Install the official Setup.exe described in the client setup documentation, then run this again.'
}
$LauncherPath = (Resolve-Path -LiteralPath $LauncherPath).ProviderPath
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

if (Test-Path -LiteralPath $ConfigPath) {
    $config = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($config -isnot [pscustomobject]) {
        throw 'Mushroom settings must contain a JSON object; refusing to replace invalid settings.'
    }
} else {
    $config = [pscustomobject]@{
        language = 'en'
        audioEnabled = $false
        audioVolume = 25
        modDeveloper = $false
    }
}
$servers = @()
if ($config.PSObject.Properties['servers']) {
    if ($config.servers -isnot [Array]) {
        throw 'Mushroom servers must be a JSON array; refusing to replace invalid settings.'
    }
    $servers = @($config.servers)
}
$matches = @($servers | Where-Object { $_.ip -eq $LoginHost -and $_.port -eq $LoginPort })
if ($matches.Count -eq 0) {
    $servers += [pscustomobject]@{
        id = [Guid]::NewGuid().ToString('N')
        name = $ServerName
        ip = $LoginHost
        port = $LoginPort
        lastPlayed = 0
        hidden = $false
        online = $false
    }
}
$config | Add-Member -NotePropertyName clientPath -NotePropertyValue $ClientPath -Force
$config | Add-Member -NotePropertyName servers -NotePropertyValue @($servers) -Force
$config | Add-Member -NotePropertyName autoLogin -NotePropertyValue $false -Force
$config | Add-Member -NotePropertyName enableConsole -NotePropertyValue $false -Force

$directory = Split-Path -Parent $ConfigPath
$null = New-Item -ItemType Directory -Path $directory -Force
$temporary = Join-Path $directory ("app-config-" + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, ($config | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $ConfigPath) {
        [IO.File]::Replace($temporary, $ConfigPath, [System.Management.Automation.Language.NullString]::Value)
    } else {
        [IO.File]::Move($temporary, $ConfigPath)
    }
} finally {
    if (Test-Path -LiteralPath $temporary) {
        Remove-Item -LiteralPath $temporary
    }
}

if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path $ClientPath 'Mushroom Launcher.lnk'))
    $shortcut.TargetPath = $LauncherPath
    $shortcut.WorkingDirectory = $ClientPath
    $shortcut.IconLocation = "$LauncherPath,0"
    $shortcut.Description = 'Official Mushroom Launcher for the local Maple2 client'
    $shortcut.Save()
}
Write-Host "Client configured: $ClientPath"
Write-Host "Login endpoint: ${LoginHost}:$LoginPort; automatic login and console disabled."
Write-Host 'Existing profiles and credentials are preserved. Register separately before manual game login.'
