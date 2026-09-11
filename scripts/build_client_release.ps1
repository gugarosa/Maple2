#Requires -Version 5.1

<#
.SYNOPSIS
Build a source-only client setup kit from a clean server checkout.
.DESCRIPTION
Packages only the authored configuration script, documentation and license.
Never copies the client, launcher binaries, archives, mods, credentials or .env.
The output is a setup kit, not a proprietary game installer or remote-service announcement.
#>
param(
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'release')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$revision = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) {
    throw 'Build the setup kit from the Git checkout so its source revision is recorded.'
}
$changes = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0 -or $changes.Count -ne 0) {
    throw 'Commit and validate the setup changes before packaging; the checkout must be clean.'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$name = "Maple2-Client-Setup-$($revision.Substring(0, 8))"
$directory = Join-Path $OutputDirectory $name
$zip = "$directory.zip"
if ((Test-Path -LiteralPath $directory) -or (Test-Path -LiteralPath $zip)) {
    throw "Release already exists: $name. Refusing to overwrite an artifact."
}
$null = New-Item -ItemType Directory -Path $directory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'configure_client.ps1') -Destination (Join-Path $directory 'Configure-Client.ps1')
Copy-Item -LiteralPath (Join-Path $root 'CLIENT_SETUP.md') -Destination (Join-Path $directory 'README.md')
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $directory 'LICENSE')
[IO.File]::WriteAllText((Join-Path $directory 'SOURCE.txt'),
    "https://github.com/gugarosa/Maple2/tree/$revision`r`n", [Text.UTF8Encoding]::new($false))
Compress-Archive -LiteralPath $directory -DestinationPath $zip
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`r`n", [Text.UTF8Encoding]::new($false))
Write-Host "Source-only setup kit: $zip"
Write-Host 'Original game/launcher binaries and private configuration are not included.'
