#Requires -Version 5.1

<#
.SYNOPSIS
Deploy the authored pilot information site to the isolated Maple2 Static Web App.
.DESCRIPTION
Requires a clean checkout at the exact origin/master revision. No game/client
assets, credentials, API backend, or existing MapleTime site are uploaded.
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
    foreach ($file in @('index.html', 'styles.css', 'staticwebapp.config.json')) {
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
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 15
            if ($response.StatusCode -eq 200 -and $response.Content.Contains('MapleTime MS2') -and
                $response.Content.Contains('Registration is not open yet')) {
                $ready = $true
                break
            }
        } catch [System.Net.WebException], [System.Net.Http.HttpRequestException] {
            Write-Verbose "Portal is not ready yet: $($_.Exception.Message)"
        }
        Start-Sleep -Seconds 5
    }
    if (-not $ready) {
        throw 'Uploaded portal did not become ready with the expected private-pilot content.'
    }
    Write-Host "Pilot information site deployed from $revision at $url"
    Write-Host 'Custom DNS, HTTPS bindings, registration, and public game access are separate readiness gates.'
} finally {
    $token = $null
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
