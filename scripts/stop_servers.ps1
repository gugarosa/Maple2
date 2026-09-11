#!/usr/bin/env pwsh
#Requires -Version 5.1

<#
.SYNOPSIS
Gracefully stop this checkout's Docker services without deleting containers or volumes.
.PARAMETER Service
Optional Compose service names. Omit to stop everything, including MySQL.
.EXAMPLE
pwsh .\scripts\stop_servers.ps1
.EXAMPLE
.\scripts\stop_servers.ps1 -Service world,login,web,game-ch0,game-ch1
#>
param([string[]]$Service)

. (Join-Path $PSScriptRoot 'compose.ps1')

if ($Service -and $Service.Count -gt 0) {
    Write-Host "Stopping services: $($Service -join ', ')"
    Invoke-Compose stop @Service
} else {
    Write-Host 'Stopping all services in this Compose project; persistent data is retained.'
    Invoke-Compose stop
}

Invoke-Compose ps --all
