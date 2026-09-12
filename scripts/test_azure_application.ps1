#Requires -Version 5.1
param([string]$WebAssembly)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$saved = @{}
$values = @{
    DB_PASSWORD = 'configuration-test-only'
    MYSQL_ROOT_PASSWORD = 'different-root-test-only'
    PUBLIC_IP = '192.0.2.10'
    MS2_DOMAIN = 'ms2.example.org'
    GAME_IMAGE = 'maple2-test/game:release'
    WORLD_IMAGE = 'maple2-test/world:release'
    LOGIN_IMAGE = 'maple2-test/login:release'
    WEB_IMAGE = 'maple2-test/web:release'
    MYSQL_IMAGE = 'mysql:8.0'
    PROXY_IMAGE = 'caddy:2-alpine'
}
foreach ($name in $values.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $values[$name], 'Process')
}
try {
    $json = docker compose --file (Join-Path $root 'deploy\azure\compose.application.yml') config --format json
    if ($LASTEXITCODE -ne 0) { throw 'Application Compose configuration is invalid.' }
    $config = $json | ConvertFrom-Json
    $services = $config.services
    if (@($services.PSObject.Properties).Count -ne 7) { throw 'Expected six application services and the TLS proxy.' }
    if ($services.mysql.PSObject.Properties['ports']) { throw 'MySQL must have no host-published port.' }
    foreach ($name in @('world', 'login', 'web', 'game-ch0', 'game-ch1')) {
        $service = $services.$name
        if ($service.environment.DB_USER -ne 'ms2_runtime' -or
            $service.environment.DB_PASSWORD -ne $values.DB_PASSWORD -or
            $service.environment.PSObject.Properties['MYSQL_ROOT_PASSWORD'] -or
            $service.PSObject.Properties['env_file']) {
            throw "Root database credentials reached an application service: $name"
        }
    }
    if ($services.web.environment.WEB_BIND_PORT -ne '4001' -or
        $services.web.environment.WEB_PORT -ne '4000' -or
        $services.web.environment.REQUIRE_HTTPS_REGISTRATION -ne 'true' -or
        $services.web.environment.PLAYER_WEBSITE_URL -ne 'https://ms2.mapletime.dev/#getting-started' -or
        $services.proxy.network_mode -ne 'service:web') {
        throw 'Web proxy, native port or player setup-link configuration is incorrect.'
    }
    if (@($services.world.ports | Where-Object { $_.host_ip -ne '127.0.0.1' }).Count) {
        throw 'World management must remain host-loopback only.'
    }
    foreach ($name in @('game-ch0', 'game-ch1')) {
        $nav = @($services.$name.volumes | Where-Object target -eq '/app/Navmeshes')
        if ($nav.Count -ne 1 -or $nav[0].read_only -ne $true) { throw 'Game navigation must be read-only.' }
    }
    $memory = ($services.PSObject.Properties.Value | Measure-Object mem_limit -Sum).Sum
    if ($memory -gt 3.5GB) { throw 'Application memory limits leave insufficient room on the 4-GiB pilot.' }
    foreach ($project in @('Game', 'World', 'Login', 'Web')) {
        $dockerfile = Get-Content -LiteralPath (Join-Path $root "Maple2.Server.$project\Dockerfile") -Raw
        if ($dockerfile -notmatch 'ARG BUILD_CONFIGURATION=Debug' -or
            $dockerfile -notmatch '-c "\$BUILD_CONFIGURATION" -o /app/publish') {
            throw 'Cloud Release builds must be selectable without changing the local Debug default.'
        }
    }
    $proxy = Get-Content -LiteralPath (Join-Path $root 'deploy\azure\Caddyfile') -Raw
    foreach ($marker in @('disable_tlsalpn_challenge', 'reverse_proxy 127.0.0.1:4001',
        'http://:80', 'http://:4000', '/irrq.aspx', '/ruq.aspx', '/urq.aspx',
        '/system/*', '/data/*', '/item/*', '/itemicon/*', '/banner/*', '/guildmark/*', '/blueprint/*')) {
        if (-not $proxy.Contains($marker)) { throw "Missing native/TLS proxy contract: $marker" }
    }
    if ($proxy -match 'trusted_proxies|tls_insecure_skip_verify') {
        throw 'The TLS proxy must not trust external forwarding headers or skip certificate verification.'
    }
    if ($WebAssembly) {
        $assembly = (Resolve-Path -LiteralPath $WebAssembly).ProviderPath
        foreach ($name in @('SERVER_SOURCE_URL', 'PLAYER_WEBSITE_URL')) {
            foreach ($url in @('http://example.org', 'https://user:password@example.org', 'javascript:alert(1)', 'not a URL')) {
                $start = [Diagnostics.ProcessStartInfo]::new()
                $start.FileName = (Get-Command dotnet -ErrorAction Stop).Source
                $start.Arguments = '"' + $assembly + '"'
                $start.WorkingDirectory = Split-Path -Parent $assembly
                $start.UseShellExecute = $false
                $start.CreateNoWindow = $true
                $start.RedirectStandardOutput = $true
                $start.RedirectStandardError = $true
                $start.EnvironmentVariables['DOTNET_RUNNING_IN_CONTAINER'] = 'true'
                $start.EnvironmentVariables['WEB_BIND_PORT'] = '4001'
                $start.EnvironmentVariables.Remove('SERVER_SOURCE_URL')
                $start.EnvironmentVariables.Remove('PLAYER_WEBSITE_URL')
                $start.EnvironmentVariables[$name] = $url
                $process = [Diagnostics.Process]::Start($start)
                try {
                    $stdout = $process.StandardOutput.ReadToEndAsync()
                    $stderr = $process.StandardError.ReadToEndAsync()
                    if (-not $process.WaitForExit(15000)) {
                        Stop-Process -Id $process.Id
                        $process.WaitForExit()
                        throw 'Web URL validation did not reject invalid configuration before startup.'
                    }
                    $output = $stdout.Result + $stderr.Result
                    if ($process.ExitCode -eq 0 -or
                        -not $output.Contains("$name must be an absolute HTTPS URL without embedded credentials.")) {
                        throw "Web did not explicitly reject invalid $name configuration."
                    }
                } finally { $process.Dispose() }
            }
        }
        Write-Host 'Compiled Web startup rejected insecure, credential-bearing and malformed player/source URLs.'
    } else {
        Write-Host 'Compiled Web URL checks were not run; supply -WebAssembly after building the Web project.'
    }
    Write-Host 'Azure application composition checks passed without cloud access or service changes.'
} finally {
    foreach ($name in $values.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
    }
}
