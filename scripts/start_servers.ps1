#!/usr/bin/env pwsh

param(
  [int[]]$NonInstancedChannels,
  [switch]$IncludeInstanced,
  [switch]$NoBuild,
  [switch]$GameOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-ComposeV2 {
  try { $null = & docker compose version 2>$null; return $LASTEXITCODE -eq 0 } catch { return $false }
}
$UseV2 = Test-ComposeV2

function Compose {
  param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Args)
  if ($UseV2) { & docker 'compose' @Args } else { & docker-compose @Args }
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

if (-not $PSBoundParameters.ContainsKey('NonInstancedChannels') -or -not $NonInstancedChannels) {
  $NonInstancedChannels = @(1)
}
if (-not $PSBoundParameters.ContainsKey('IncludeInstanced')) {
  $IncludeInstanced = $true
}

$gameServices = @()
if ($IncludeInstanced) { $gameServices += 'game-ch0' }
foreach ($ch in $NonInstancedChannels) { $gameServices += "game-ch$ch" }

if ($GameOnly -and -not $PSBoundParameters.ContainsKey('NoBuild')) {
  $NoBuild = $true
}

if (-not $NoBuild) {
  if ($GameOnly) {
    if ($gameServices.Count -eq 0) { $gameServices = @('game-ch0') }
    Write-Host "Building game images only: $($gameServices -join ', ')"
    Compose (@('build','--pull') + $gameServices)
  } else {
    Write-Host "Building images..."
    Compose @('build','--pull')
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
if ($gameServices.Count -eq 0) { $gameServices = @('game-ch0') }

$upArgs = @('up','--detach')
if ($GameOnly) { $upArgs += @('--no-deps','--force-recreate') }
if (-not $NoBuild -and $UseV2) { $upArgs += '--build' }

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
Write-Host "All services started. Tail logs with:"
if ($GameOnly) {
  $joined = ($started) -join ' '
} else {
  $joined = ($started + @('world','login')) -join ' '
}
if ($UseV2) { Write-Host "  docker compose logs -f $joined" }
else { Write-Host "  docker-compose logs -f $joined" }