#Requires -Version 5.1

<#
.SYNOPSIS
Check orchestration without Docker, builds, servers, credentials, or client data.
.DESCRIPTION
Uses an in-process Docker stub and a disposable fixture inside this checkout.
Run with: pwsh .\scripts\test_operations.ps1
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

$root = Split-Path -Parent $PSScriptRoot
foreach ($path in @(
    (Join-Path $root 'setup.ps1'),
    (Join-Path $PSScriptRoot 'compose.ps1'),
    (Join-Path $PSScriptRoot 'start_servers.ps1'),
    (Join-Path $PSScriptRoot 'stop_servers.ps1')
)) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    Assert ($errors.Count -eq 0) "PowerShell parse failed: $path"
}

$state = @{
    Calls = [System.Collections.Generic.List[object]]::new()
    Failure = ''
    Unhealthy = ''
}
$oldExitCode = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
Push-Location -LiteralPath $root
$fixture = $null
try {
    $fixture = (New-Item -ItemType Directory -Path ".operations-check $([guid]::NewGuid().ToString('N'))").FullName
    $fixtureScripts = Join-Path $fixture 'scripts'
    $null = New-Item -ItemType Directory -Path $fixtureScripts
    foreach ($file in @('compose.ps1', 'start_servers.ps1', 'stop_servers.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination $fixtureScripts
    }
    $fixtureEnv = Join-Path $fixture '.env'
    Set-Content -LiteralPath $fixtureEnv -Value '# No credentials; Docker is stubbed.'
    $start = Join-Path $fixtureScripts 'start_servers.ps1'
    $stop = Join-Path $fixtureScripts 'stop_servers.ps1'

    function Invoke-TestDocker {
        param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

        $global:LASTEXITCODE = 0
        if ($Arguments -contains 'version') {
            '2.20.0'
            return
        }
        Assert ($Arguments[0] -eq 'compose') 'Only Compose commands are expected.'
        Assert ($Arguments[1] -eq '--project-directory' -and $Arguments[2] -eq $fixture) 'Wrong checkout or split path with spaces.'
        Assert ($Arguments[3] -eq '--env-file' -and $Arguments[4] -eq $fixtureEnv) 'Wrong environment file.'
        Assert ($Arguments[5] -eq '--file' -and $Arguments[6] -eq (Join-Path $fixture 'compose.yml')) 'Wrong Compose file.'
        $command = $Arguments[7]
        $state.Calls.Add([pscustomobject]@{ Command = $command; Arguments = $Arguments })
        if ($state.Failure -eq $command) {
            $global:LASTEXITCODE = 29
            return
        }
        switch ($command) {
            'config' { @('mysql', 'world', 'login', 'web', 'game-ch0', 'game-ch1') }
            'ps' {
                if ($Arguments -contains '--format') {
                    foreach ($service in @('mysql', 'world', 'login', 'web')) {
                        $health = 'healthy'
                        if ($state.Unhealthy -eq $service) {
                            $health = 'unhealthy'
                        }
                        "$service running $health"
                    }
                }
            }
        }
    }
    Set-Alias -Name docker -Value Invoke-TestDocker -Scope Local

    function Expect-Failure {
        param([scriptblock]$Action)
        $failed = $false
        try {
            & $Action *> $null
        } catch {
            $failed = $true
        }
        Assert $failed 'A failed prerequisite or Docker command must stop orchestration.'
    }

    # Deliberately run from outside the fixture checkout.
    & $start -NoBuild *> $null
    $ups = @($state.Calls | Where-Object Command -eq 'up')
    Assert ($ups.Count -eq 3) 'Expected infrastructure and two ordered game starts.'
    Assert (@($state.Calls | Where-Object Command -eq 'build').Count -eq 0) '-NoBuild invoked a build.'
    foreach ($up in $ups) {
        Assert ($up.Arguments -contains '--wait' -and $up.Arguments -contains '--wait-timeout') 'Readiness must be bounded by Compose.'
        Assert ($up.Arguments -contains '--no-build') '-NoBuild allowed an implicit image build.'
    }
    Assert ($ups[1].Arguments[-1] -eq 'game-ch0' -and $ups[2].Arguments[-1] -eq 'game-ch1') 'Channel startup order changed.'
    Assert ($ups[1].Arguments -contains '--force-recreate' -and $ups[2].Arguments -contains '--force-recreate') 'Games must re-register after a World replacement.'

    $state.Calls.Clear()
    & $start *> $null
    $builds = @($state.Calls | Where-Object Command -eq 'build')
    Assert ($builds.Count -eq 1) 'Build images once, before startup.'
    Assert ($builds[0].Arguments -contains 'game-ch0' -and $builds[0].Arguments -notcontains 'game-ch1') 'Both channels share one game image build.'
    Assert ($builds[0].Arguments -notcontains 'file-ingest') 'Normal startup must not build or run ingestion.'

    $state.Calls.Clear()
    & $start -GameOnly -NoBuild *> $null
    $ups = @($state.Calls | Where-Object Command -eq 'up')
    Assert ($ups.Count -eq 2) 'Game-only mode touched infrastructure.'
    foreach ($up in $ups) {
        Assert ($up.Arguments -contains '--no-deps' -and $up.Arguments -contains '--force-recreate') 'Game-only restart must not restart dependencies.'
    }
    Assert (@($state.Calls | Where-Object Command -eq 'rm').Count -eq 0) 'Compose recreation must not be preceded by rm/sleep.'

    $state.Calls.Clear()
    $state.Unhealthy = 'world'
    Expect-Failure { & $start -GameOnly }
    Assert (@($state.Calls | Where-Object { $_.Command -in @('build', 'up') }).Count -eq 0) 'Unhealthy infrastructure was changed.'
    $state.Unhealthy = ''

    $state.Calls.Clear()
    Expect-Failure { & $start -NonInstancedChannels 2 }
    Assert (@($state.Calls | Where-Object { $_.Command -in @('build', 'up') }).Count -eq 0) 'Undefined channels must fail before changes.'

    foreach ($command in @('config', 'build', 'up', 'stop')) {
        $state.Calls.Clear()
        $state.Failure = $command
        if ($command -eq 'stop') {
            Expect-Failure { & $stop }
        } else {
            Expect-Failure { & $start }
        }
        Assert ($state.Calls[-1].Command -eq $command) "Execution continued after $command failed."
    }
    $state.Failure = ''

    $state.Calls.Clear()
    & $stop -Service @('world', 'login', 'web', 'game-ch0', 'game-ch1') *> $null
    $stops = @($state.Calls | Where-Object Command -eq 'stop')
    Assert ($stops.Count -eq 1 -and $stops[0].Arguments -notcontains 'mysql') 'Metadata-refresh stop must preserve MySQL.'

    $state.Calls.Clear()
    Remove-Item -LiteralPath $fixtureEnv
    Expect-Failure { & $start -NoBuild }
    Assert ($state.Calls.Count -eq 0) 'Missing .env must fail before Docker operations.'

    Write-Host 'Operational checks passed; no Docker process, server, build, or real data was used.'
} finally {
    Pop-Location
    if ($fixture) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
    if ($oldExitCode) {
        $global:LASTEXITCODE = $oldExitCode.Value
    } else {
        Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
}
