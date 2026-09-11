#Requires -Version 5.1

<#
.SYNOPSIS
Validate local .NET/MySQL configuration and ingest metadata.
.DESCRIPTION
Configure .env first and stop application servers before running. Client archives
are inputs only: this script never downloads or overwrites them. Ingestion uses
the repository's .NET 8 SDK selection and local EF Core 7.0.20 tool manifest.
For Docker setup, use the explicit Compose ingest profile instead.
.PARAMETER RunNavmesh
Also generate navmeshes during ingestion.
#>
param([switch]$RunNavmesh)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Push-Location -LiteralPath $PSScriptRoot
try {
    $envPath = '.env'
    if (-not [string]::IsNullOrWhiteSpace($env:DOTENV_PATH)) {
        $envPath = $env:DOTENV_PATH
    }
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
        if ($envPath -eq '.env') {
            Copy-Item -LiteralPath '.env.example' -Destination '.env'
        }
        throw "Configure $envPath with your client Data path and MySQL credentials, then run setup again."
    }
    $envPath = (Resolve-Path -LiteralPath $envPath).ProviderPath

    $settings = @{}
    foreach ($line in Get-Content -LiteralPath $envPath) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$') {
            $key = $Matches[1]
            $value = $Matches[2].Trim()
            if ($value.Length -ge 2 -and (
                ($value.StartsWith("'") -and $value.EndsWith("'")) -or
                ($value.StartsWith('"') -and $value.EndsWith('"')))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            $settings[$key] = $value
        }
    }

    foreach ($key in @('MS2_DATA_FOLDER', 'LANGUAGE', 'DB_IP', 'DB_PORT', 'DB_USER', 'DB_PASSWORD', 'DATA_DB_NAME', 'GAME_DB_NAME')) {
        $existing = [Environment]::GetEnvironmentVariable($key)
        if (-not [string]::IsNullOrEmpty($existing)) {
            $settings[$key] = $existing
        }
        if ([string]::IsNullOrWhiteSpace($settings[$key])) {
            throw "Set $key in $envPath or the process environment before running setup."
        }
    }
    [ushort]$dbPort = 0
    if (-not [ushort]::TryParse($settings['DB_PORT'], [ref]$dbPort) -or $dbPort -eq 0) {
        throw 'DB_PORT must be an integer from 1 to 65535.'
    }
    if ($settings['DATA_DB_NAME'] -eq $settings['GAME_DB_NAME']) {
        throw 'DATA_DB_NAME and GAME_DB_NAME must be different. Metadata refresh must never target player data.'
    }

    $dataPath = (Resolve-Path -LiteralPath $settings['MS2_DATA_FOLDER']).ProviderPath
    foreach ($archive in @(
        'Xml', 'Server', 'Resource\Exported', 'Resource\Library',
        'Resource\Model\Map', 'Resource\Model\Effect', 'Resource\Model\Camera',
        'Resource\Model\Tool', 'Resource\Model\Item', 'Resource\Model\Npc',
        'Resource\Model\Path', 'Resource\Model\Character', 'Resource\Model\Textures'
    )) {
        foreach ($extension in @('.m2d', '.m2h')) {
            $path = Join-Path $dataPath "$archive$extension"
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Missing archive: $path. Supply the original client data and customized Server.m2d/Server.m2h; setup never overwrites client files."
            }
        }
    }

    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw 'Install the .NET 8 SDK and its Microsoft.NETCore.App and Microsoft.AspNetCore.App 8.x runtimes.'
    }
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or "$sdk" -notmatch '^8\.') {
        throw 'Install a stable .NET 8 SDK compatible with global.json; do not bypass the SDK selection.'
    }
    $runtimes = & dotnet --list-runtimes
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to list .NET shared runtimes.'
    }
    foreach ($runtime in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
        if (-not ($runtimes -match "^$([regex]::Escape($runtime)) 8\.")) {
            throw "Missing $runtime 8.x. Install the .NET 8 SDK with its shared runtimes."
        }
    }

    $ingestArguments = @()
    if ($RunNavmesh) {
        $ingestArguments += '--run-navmesh'
    }
    $ingestProject = Join-Path 'Maple2.File.Ingest' 'Maple2.File.Ingest.csproj'
    $previousEnvPath = $env:DOTENV_PATH
    $previousDataPath = $env:MS2_DATA_FOLDER
    try {
        # dotnet run uses the project's working directory, not necessarily this one.
        $env:DOTENV_PATH = $envPath
        $env:MS2_DATA_FOLDER = $dataPath
        & dotnet run --project $ingestProject -- @ingestArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Metadata ingestion failed (exit $LASTEXITCODE). Check the migration/database error above; do not delete the MySQL volume."
        }
    } finally {
        $env:DOTENV_PATH = $previousEnvPath
        $env:MS2_DATA_FOLDER = $previousDataPath
    }

    Write-Host 'Metadata ingestion completed. Start the Docker stack or your individual development services.'
} finally {
    Pop-Location
}
