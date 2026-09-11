#!/usr/bin/env pwsh
#Requires -Version 5.1

<#
.SYNOPSIS
Build and start the Docker stack, or restart its game channels.
.DESCRIPTION
Requires configured .env and previously ingested metadata. Never runs ingestion
or removes database volumes. Docker owns the detached containers after this
script exits. Any build, startup, or readiness failure is an error.
.PARAMETER NonInstancedChannels
Normal channels defined in compose.yml; the supplied topology provides channel 1.
.PARAMETER NoInstanced
Skip game-ch0. Omit this for the complete playable topology.
.PARAMETER NoBuild
Use existing application images without building them.
.PARAMETER GameOnly
Recreate only the selected game channels; infrastructure must already be healthy.
.EXAMPLE
pwsh .\scripts\start_servers.ps1
.EXAMPLE
pwsh .\scripts\start_servers.ps1 -GameOnly -NoBuild
#>
param(
    [ValidateRange(1, 99)][int[]]$NonInstancedChannels = @(1),
    [switch]$NoInstanced,
    [switch]$NoBuild,
    [switch]$GameOnly
)

. (Join-Path $PSScriptRoot 'compose.ps1')

$gameServices = @()
if (-not $NoInstanced) {
    $gameServices += 'game-ch0'
}
foreach ($channel in ($NonInstancedChannels | Sort-Object -Unique)) {
    $gameServices += "game-ch$channel"
}
if ($gameServices.Count -eq 0) {
    throw 'Select at least one game channel.'
}

$definedServices = @(Invoke-Compose config --services)
foreach ($service in $gameServices) {
    if ($service -notin $definedServices) {
        throw "$service is not defined in compose.yml. The supplied topology has game-ch0 and game-ch1."
    }
}

$infrastructure = @('mysql', 'world', 'login', 'web')
if ($GameOnly) {
    $states = @(Invoke-Compose ps --all --format '{{.Service}} {{.State}} {{.Health}}' @infrastructure)
    foreach ($service in $infrastructure) {
        if ("$service running healthy" -notin $states) {
            throw "Game-only restart requires healthy $service. Run a full startup first; no containers were changed."
        }
    }
}

if (-not $NoBuild) {
    # Both game services use the same image; build it once, before changing containers.
    $buildServices = @($gameServices[0])
    if (-not $GameOnly) {
        $buildServices += @('world', 'login', 'web')
    }
    Invoke-Compose build @buildServices
}

if (-not $GameOnly) {
    Invoke-Compose up --detach --no-build --wait --wait-timeout 300 @infrastructure
}

# Recreate games even on full startup: a replaced World has lost its registrations.
# Compose stops each old container gracefully; no rm/sleep race or dependency restart.
foreach ($service in $gameServices) {
    Invoke-Compose up --detach --no-deps --no-build --pull never --force-recreate --wait --wait-timeout 300 $service
}

Invoke-Compose ps
Write-Host 'Containers passed their readiness checks. Client login remains a separate end-to-end check.'
Write-Host "Tail logs from $ComposeRoot with: docker compose logs -f world login $($gameServices -join ' ')"
