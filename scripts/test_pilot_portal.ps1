#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$site = Join-Path $root 'website'
$expected = @('index.html', 'staticwebapp.config.json', 'styles.css')
$actual = @(Get-ChildItem -LiteralPath $site -File | Select-Object -ExpandProperty Name | Sort-Object)
if (($actual -join ',') -ne ($expected -join ',')) {
    throw 'Pilot site includes files outside its authored static allowlist.'
}
$html = Get-Content -LiteralPath (Join-Path $site 'index.html') -Raw -Encoding UTF8
foreach ($marker in @('MapleTime MS2', 'Registration is not open yet', 'not a live server-health monitor', 'noindex, nofollow')) {
    if (-not $html.Contains($marker)) { throw "Pilot page is missing its readiness boundary: $marker" }
}
if ($html -match '(?i)<(?:form|input|script)\b|(?:src|href)=[\"''][^\"'']*localhost|(?:src|href)=[\"'']http://') {
    throw 'Pilot page must not collect credentials, run scripts, or advertise local/insecure endpoints.'
}
$settings = Get-Content -LiteralPath (Join-Path $site 'staticwebapp.config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($settings.globalHeaders.'Content-Security-Policy' -notmatch "form-action 'none'" -or
    $settings.globalHeaders.'X-Content-Type-Options' -ne 'nosniff') {
    throw 'Pilot portal security headers are incomplete.'
}
$tokens = $null
$errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'deploy\azure\deploy-portal.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Portal deployment script does not parse.' }
Write-Host 'Pilot portal content and deployment checks passed without cloud access.'
