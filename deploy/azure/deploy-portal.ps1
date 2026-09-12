#Requires -Version 5.1

<#
.SYNOPSIS
Deploy the authored pilot information site to the isolated Maple2 Static Web App.
.DESCRIPTION
Requires a clean checkout at the exact origin/master revision. Uploads only the
website and user-authorized promotional artwork. No client binaries, archives,
credentials, API backend, or existing MapleTime site are uploaded.
#>
param(
    [Parameter(Mandatory = $true)][Guid]$SubscriptionId,
    [Parameter(Mandatory = $true)][string]$StaticWebAppName,
    [string]$ResourceGroup = 'rg-maple2-brazilsouth',
    [string]$StaticSitesClientImage = 'mcr.microsoft.com/appsvc/staticappsclient:stable'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$subscription = $SubscriptionId.ToString()
function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments)
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Command failed with exit code $LASTEXITCODE."
    }
}
function Test-PortalFile {
    param([object]$Response, [string]$Path)
    if ($Response.StatusCode -ne 200) { return $false }
    $Response.RawContentStream.Position = 0
    $actual = (Get-FileHash -InputStream $Response.RawContentStream -Algorithm SHA256).Hash
    $expected = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    return $actual -eq $expected
}
foreach ($command in @('az', 'docker', 'git')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "Required command is missing: $command"
    }
}
$changes = @(Invoke-Checked git @('-C', $root, 'status', '--porcelain'))
if ($changes.Count -ne 0) {
    throw 'Publish and validate a clean source revision before deploying the pilot site.'
}
Invoke-Checked git @('-C', $root, 'fetch', '--quiet', 'origin')
$revision = Invoke-Checked git @('-C', $root, 'rev-parse', 'HEAD')
$main = Invoke-Checked git @('-C', $root, 'rev-parse', 'origin/master')
if ($revision -ne $main) {
    throw 'Portal deployment requires the exact current origin/master revision.'
}
& (Join-Path $root 'scripts\test_pilot_portal.ps1')
$site = Invoke-Checked az @(
    'staticwebapp', 'show', '--subscription', $subscription,
    '--resource-group', $ResourceGroup, '--name', $StaticWebAppName,
    '--only-show-errors', '--output', 'json'
) | ConvertFrom-Json
if ($null -eq $site.tags -or -not $site.tags.PSObject.Properties['project'] -or $site.tags.project -ne 'maple2') {
    throw 'Refusing to deploy to a Static Web App not tagged as Maple2.'
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('Maple2Portal-' + [Guid]::NewGuid().ToString('N'))
$content = Join-Path $temporary 'site'
$envFile = Join-Path $temporary 'deployment.env'
$null = New-Item -ItemType Directory -Path $content
try {
    foreach ($file in @(
        'index.html', 'styles.css', 'staticwebapp.config.json', 'mark.svg',
        'ms2-logo.png', 'ms2-world.webp', 'ms2-world-mobile.webp',
        'ms2-slime.webp', 'ms2-pig.webp', 'ms2-mushroom.webp'
    )) {
        Copy-Item -LiteralPath (Join-Path (Join-Path $root 'website') $file) -Destination $content
    }
    $token = Invoke-Checked az @(
        'staticwebapp', 'secrets', 'list', '--subscription', $subscription,
        '--resource-group', $ResourceGroup, '--name', $StaticWebAppName,
        '--query', 'properties.apiKey', '--only-show-errors', '--output', 'tsv'
    )
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw 'The portal deployment token is unavailable.'
    }
    @(
        'GITHUB_WORKSPACE=/github/workspace'
        'INPUT_ACTION=upload'
        'INPUT_APP_LOCATION=site'
        'INPUT_API_LOCATION='
        'INPUT_OUTPUT_LOCATION='
        'INPUT_SKIP_APP_BUILD=true'
        'INPUT_SKIP_API_BUILD=true'
        "INPUT_AZURE_STATIC_WEB_APPS_API_TOKEN=$token"
    ) | Set-Content -LiteralPath $envFile -Encoding ascii
    $token = $null
    Invoke-Checked docker @(
        'run', '--rm', '--volume', "${temporary}:/github/workspace:ro",
        '--env-file', $envFile, '--entrypoint', '/bin/staticsites/StaticSitesClient',
        $StaticSitesClientImage, 'upload'
    )
    $url = "https://$($site.defaultHostname)"
    $ready = $false
    for ($attempt = 0; $attempt -lt 24; $attempt++) {
        try {
            $ready = $true
            foreach ($file in @(
                'index.html', 'styles.css', 'mark.svg', 'ms2-logo.png',
                'ms2-world.webp', 'ms2-world-mobile.webp',
                'ms2-slime.webp', 'ms2-pig.webp', 'ms2-mushroom.webp'
            )) {
                $path = if ($file -eq 'index.html') { '/' } else { "/$file" }
                $response = Invoke-WebRequest -Uri "$url$path" -UseBasicParsing -TimeoutSec 15
                if (-not (Test-PortalFile -Response $response -Path (Join-Path $content $file))) {
                    Write-Verbose "Waiting for the uploaded revision of $file."
                    $ready = $false
                    break
                }
            }
            if ($ready) { break }
        } catch [System.Net.WebException], [System.Net.Http.HttpRequestException] {
            $ready = $false
            Write-Verbose "Portal is not ready yet: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 5
    }
    if (-not $ready) {
        throw 'Uploaded portal did not become ready with the exact expected HTML, stylesheet, and artwork.'
    }
    Write-Host "Pilot information site deployed from $revision at $url"
    Write-Host 'Custom DNS, HTTPS bindings, registration, and public game access are separate readiness gates.'
} finally {
    $token = $null
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
