# Shared by the Docker start/stop entrypoints; all paths belong to this checkout.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ComposeRoot = Split-Path -Parent $PSScriptRoot
$ComposeFile = Join-Path $ComposeRoot 'compose.yml'
$ComposeEnv = Join-Path $ComposeRoot '.env'

if (-not (Test-Path -LiteralPath $ComposeEnv -PathType Leaf)) {
    throw "Missing $ComposeEnv. Copy .env.example to .env and configure it first."
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker with Compose v2.20 or newer is required. Install Docker Engine or Docker Desktop.'
}

try {
    $composeVersion = & docker compose version --short 2>$null
    if ($LASTEXITCODE -ne 0 -or -not ("$composeVersion" -match '^v?(\d+\.\d+\.\d+)')) {
        throw 'Unable to read the Compose version.'
    }
    if ([version]$Matches[1] -lt [version]'2.20.0') {
        throw 'Compose is too old.'
    }
} catch {
    throw 'Docker Compose v2.20 or newer is required for bounded readiness checks. Update Docker Compose.'
}

function Invoke-Compose {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$ComposeArguments)

    & docker compose --project-directory $ComposeRoot --env-file $ComposeEnv --file $ComposeFile @ComposeArguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose $($ComposeArguments[0]) failed (exit $LASTEXITCODE). See the Docker error above."
    }
}
