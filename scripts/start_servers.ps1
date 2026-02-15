#!/usr/bin/env pwsh

<#
.SYNOPSIS
  Start Maple2 servers via Docker Compose.

.DESCRIPTION
  Builds and starts the Maple2 Docker services. By default starts everything
  (mysql, world, login, web, game-ch0, game-ch1). Use -GameOnly to restart
  just the game channel containers without touching infrastructure services.

.PARAMETER NonInstancedChannels
  Channel numbers for non-instanced game servers. Default: 1 (game-ch1).

.PARAMETER NoInstanced
  Skip the instanced-content channel (game-ch0).

.PARAMETER NoBuild
  Skip the Docker image build step. Uses whatever images already exist.

.PARAMETER GameOnly
  Only restart game channel containers. Skips mysql/world/login/web and
  uses --no-deps --force-recreate to swap game channels in-place.

.EXAMPLE
  # Start everything (default)
  pwsh ./scripts/start_servers.ps1

.EXAMPLE
  # Rebuild and restart only game channels
  pwsh ./scripts/start_servers.ps1 -GameOnly

.EXAMPLE
  # Start channels 1 and 2, no instanced channel
  pwsh ./scripts/start_servers.ps1 -NonInstancedChannels 1,2 -NoInstanced

.EXAMPLE
  # Restart game channels without rebuilding
  pwsh ./scripts/start_servers.ps1 -GameOnly -NoBuild
#>

param(
  [int[]]$NonInstancedChannels = @(1),
  [switch]$NoInstanced,
  [switch]$NoBuild,
  [switch]$GameOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Require Docker Compose v2
try { $null = & docker compose version 2>$null } catch { }
if ($LASTEXITCODE -ne 0) {
  Write-Error "Docker Compose v2 is required. Install it from https://docs.docker.com/compose/install/"
  exit 1
}

function Compose {
  param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Args)
  & docker compose @Args
}

function Wait-Healthy {
  param(
    [Parameter(Mandatory=$true)][string]$Service,
    [int]$TimeoutSec = 300,
    [switch]$Soft
  )
  Write-Host "Waiting for $Service to be healthy (timeout ${TimeoutSec}s)..."
  $start = Get-Date
  while ($true) {
    $cid = Compose ps -q $Service 2>$null | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($cid)) { Start-Sleep -Seconds 2; continue }

    $statusRaw = docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' $cid 2>$null
    $status = ("$statusRaw" | Out-String).Trim().ToLowerInvariant()

    if ($status -match 'healthy') {
      Write-Host "$Service is healthy"
      return $true
    } elseif ($status -match '^(running|starting|created)$') {
    } elseif ($status -match 'exited') {
      Write-Warning "$Service exited unexpectedly. Showing last logs:"
      try { Compose logs --no-color --tail=200 $Service } catch { }
      if ($Soft) { return $false } else { throw "$Service exited" }
    }

    if ((Get-Date) - $start -gt [TimeSpan]::FromSeconds($TimeoutSec)) {
      Write-Warning "Timeout waiting for $Service to be healthy. Logs:"
      try { Compose logs --no-color --tail=200 $Service } catch { }
      if ($Soft) { return $false } else { throw "Timeout waiting for $Service" }
    }
    Start-Sleep -Seconds 2
  }
}

function Get-DotEnv {
  param([string]$Path = ".env")
  $map = @{}
  if (Test-Path $Path) {
    foreach ($line in Get-Content $Path) {
      if ($line -match '^(\s*#|\s*$)') { continue }
      $kv = $line -split '=',2
      if ($kv.Count -eq 2) { $map[$kv[0].Trim()] = $kv[1].Trim() }
    }
  }
  return $map
}

$envMap = Get-DotEnv
$gameIp = $envMap['GAME_IP']
if (-not $gameIp) {
  Write-Warning "GAME_IP is not set in .env. Clients may receive 127.0.0.1 and fail to connect."
} elseif ($gameIp -match '^(127\.0\.0\.1|localhost)$') {
  Write-Warning "GAME_IP is set to $gameIp. External clients will fail. Set GAME_IP to your host/LAN IP in .env."
}

$gameServices = @()
if (-not $NoInstanced) { $gameServices += 'game-ch0' }
foreach ($ch in $NonInstancedChannels) { $gameServices += "game-ch$ch" }
if ($gameServices.Count -eq 0) { $gameServices = @('game-ch0') }

if (-not $NoBuild) {
  if ($GameOnly) {
    Write-Host "Building game images only: $($gameServices -join ', ')"
    Compose (@('build') + $gameServices)
  } else {
    Write-Host "Building all images..."
    Compose @('build')
  }
}

if (-not $GameOnly) {
  Write-Host "Starting database..."
  Compose @('up','--detach','mysql')
  Wait-Healthy -Service mysql -TimeoutSec 300

  Write-Host "Starting world, login, and web..."
  Compose @('up','--detach','world','login','web')
  Wait-Healthy -Service world -TimeoutSec 300
  Wait-Healthy -Service login -TimeoutSec 300
} else {
  Write-Host "Game-only mode: skipping database/world/login/web startup."
}

Write-Host "Starting game channels..."
$started = @()

$upArgs = @('up','--detach')
if ($GameOnly) { $upArgs += @('--no-deps','--force-recreate') }
if (-not $NoBuild) { $upArgs += '--build' }

foreach ($svc in $gameServices) {
  if ($GameOnly) {
    try { Compose @('rm','-s','-f', $svc) } catch { }
    Start-Sleep -Seconds 2
  }
  Compose ($upArgs + $svc)
  $null = Wait-Healthy -Service $svc -TimeoutSec 300 -Soft
  $started += $svc
}

Write-Host
Compose ps
Write-Host
$joined = ($GameOnly ? $started : ($started + @('world','login'))) -join ' '
Write-Host "All services started. Tail logs with:"
Write-Host "  docker compose logs -f $joined"