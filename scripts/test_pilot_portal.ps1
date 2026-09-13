#Requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$site = Join-Path $root 'website'
$artwork = @{
    'ms2-logo.png' = 'B7AE33C43D14CCEB6725965C6FE6CC870E6BD4D743A41A6DEBA984F8E0991A71'
    'ms2-logo.webp' = '480DC98B67C55547BBB4430A834EFD87DCC0EFA333AFBABD69A39B2DB0FADA80'
    'ms2-world.webp' = '7F8564037F5F4ABE64CD9AD98785F7EA6B5A5BE3C318FCD461CD71FA098808A6'
    'ms2-world-mobile.webp' = '41A13D6E5ED4046AC587C19113C775763A1C904EA386818B47C80358611DC622'
    'ms2-slime.webp' = '7562CA6202EFDEF433A7DCCCE1A681EBE2DCFFD1E7743B7C0CF4A9738652D7E1'
    'ms2-pig.webp' = '5616D7F06D6D7016F28B5692658A94209A3D554BBC185B206E2F910E61328751'
    'ms2-mushroom.webp' = '71E59AD74C7BFA4FD40B75635DF2C55D5D26E45120A9BCAF0DCA8FF1170ED17E'
}
$expected = @(@('index.html', '404.html', 'mark.svg', 'staticwebapp.config.json', 'theme.css', 'styles.css') + @($artwork.Keys) | Sort-Object)
$actual = @(Get-ChildItem -LiteralPath $site -Recurse -File |
    ForEach-Object { $_.FullName.Substring($site.Length + 1) } | Sort-Object)
if (($actual -join ',') -ne ($expected -join ',')) {
    throw 'Pilot site includes files outside its authored static allowlist.'
}
$html = Get-Content -LiteralPath (Join-Path $site 'index.html') -Raw -Encoding UTF8
$distribution = Get-Content -LiteralPath (Join-Path $root 'installer\distribution.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($marker in @(
    'MapleTime MS2', 'Registration and play are invite-only', 'Your network must be approved',
    'The Azure pilot is running with restricted access', 'not a live server-health monitor',
    'does not collect passwords', 'MS1 accounts and characters do not carry over',
    'MapleStory 2 artwork &copy; NEXON', 'Artwork credits', 'noindex, nofollow',
    'installer is on hold for antivirus review', 'No game-client download is published here',
    'Mushroom is the launcher, not the game-client download', 'Leave automatic login off',
    'Installer review, client distribution and full gameplay checks must pass before public launch'
)) {
    if (-not $html.Contains($marker)) { throw "Pilot page is missing its readiness boundary: $marker" }
}
if ($html -match '(?i)<(?:form|input|script)\b|(?:src|href)=[\"''][^\"'']*localhost|(?:src|href)=[\"'']http://') {
    throw 'Pilot page must not collect credentials, run scripts, or advertise local/insecure endpoints.'
}
$ids = @([regex]::Matches($html, '\bid="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
if (($ids | Sort-Object -Unique).Count -ne $ids.Count) { throw 'Player-journey anchors must be unique.' }
$links = @([regex]::Matches($html, '\bhref="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
foreach ($link in $links) {
    if ($link.StartsWith('#') -and $link.Substring(1) -notin $ids) {
        throw "Player-journey link has no target: $link"
    }
    if ($link -match '(?i)\.(exe|msi|zip|7z|rar|m2d|m2h)([?#]|$)') {
        throw 'Do not publish a game/installer download while the release gates are blocked.'
    }
}
$registrationLink = 'href="' + $distribution.registrationUrl + '" aria-describedby="pilot-access"'
if (-not $html.Contains($registrationLink) -or
    @($links | Where-Object { $_ -eq $distribution.registrationUrl }).Count -ne 1) {
    throw 'Use one real HTTPS pilot registration link with its access restriction attached.'
}
foreach ($value in @($distribution.serverName, $distribution.loginHost,
    [string]$distribution.loginPort, $distribution.clientVersion)) {
    if (-not $html.Contains(">$value<")) { throw 'Website setup details differ from the installer/client compatibility contract.' }
}
$launcherRelease = 'https://github.com/shuabritze/mushroom-launcher/releases/tag/v' + $distribution.mushroom.version
if (@($links | Where-Object { $_ -eq $launcherRelease }).Count -ne 1 -or
    $html -notmatch 'id="connection-steps"') {
    throw 'Provide one official launcher release link and in-page manual connection steps.'
}
foreach ($image in [regex]::Matches($html, '(?i)\b(?:src|srcset)=["'']([^"'']+)["'']')) {
    $source = $image.Groups[1].Value
    if (-not $source.StartsWith('/') -or $source.StartsWith('//') -or
        ($source -ne '/mark.svg' -and -not $artwork.ContainsKey($source.Substring(1)))) {
        throw 'Pilot artwork must be one of the reviewed, locally served images.'
    }
}
foreach ($asset in $artwork.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $site $asset) -Algorithm SHA256).Hash -ne $artwork[$asset]) {
        throw "Website artwork differs from its reviewed source: $asset"
    }
    if (-not $html.Contains('"/' + $asset + '"')) {
        throw "Unused artwork must not be included in the website: $asset"
    }
}
$monsters = @([regex]::Matches($html, 'class="update-monster" src="([^"]+)"') |
    ForEach-Object { $_.Groups[1].Value.TrimStart('/') } | Sort-Object -Unique)
if (($monsters -join ',') -ne 'ms2-mushroom.webp,ms2-pig.webp,ms2-slime.webp') {
    throw 'Project updates must use three distinct MS2 monsters.'
}
if ($html -notmatch '<source media="\(max-width: 60rem\)" srcset="/ms2-world-mobile.webp"') {
    throw 'Small screens must select the framed mobile MS2 artwork.'
}
if ($html -match '@guide|GMS v83|stmapletimeassets|world\.svg|Site illustrations are original|class="(?:eyebrow|step)"') {
    throw 'MS2 must not inherit MS1 game copy/artwork, draft illustration claims, or retired sublabels.'
}
foreach ($asset in @('mark.svg')) {
    $svg = Get-Content -LiteralPath (Join-Path $site $asset) -Raw -Encoding UTF8
    if ($svg -match '(?i)<!DOCTYPE|<\s*(?:script|foreignObject|image|style)\b|\son[a-z]+\s*=|\bhref\s*=') {
        throw "Artwork includes executable or external content: $asset"
    }
    $document = [xml]$svg
    if ($document.DocumentElement.LocalName -ne 'svg' -or
        $document.DocumentElement.NamespaceURI -ne 'http://www.w3.org/2000/svg') {
        throw "Artwork is not a valid standalone SVG: $asset"
    }
}
$settings = Get-Content -LiteralPath (Join-Path $site 'staticwebapp.config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($settings.mimeTypes.'.webp' -ne 'image/webp') {
    throw 'Optimized MS2 artwork must be served with the WebP image media type.'
}
if ($settings.globalHeaders.'Content-Security-Policy' -notmatch "form-action 'none'" -or
    $settings.globalHeaders.'Content-Security-Policy' -notmatch "img-src 'self'" -or
    $settings.globalHeaders.'X-Content-Type-Options' -ne 'nosniff') {
    throw 'Pilot portal security headers are incomplete.'
}
if ($settings.responseOverrides.'404'.rewrite -ne '/404.html' -or
    $settings.responseOverrides.'404'.statusCode -ne 404) {
    throw 'Missing pages must retain HTTP 404 and provide the authored recovery page.'
}
foreach ($page in @('index.html', '404.html')) {
    $markup = Get-Content -LiteralPath (Join-Path $site $page) -Raw -Encoding UTF8
    if (-not $markup.Contains('href="/theme.css"') -or -not $markup.Contains('href="/styles.css"') -or
        $markup -match '(?i)<(?:form|input|script|style)\b') {
        throw "Static pages must use the shared local styles without inline code or credential collection: $page"
    }
}
$tokens = $null
$errors = $null
$deployment = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'deploy\azure\deploy-portal.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Portal deployment script does not parse.' }
$fileLoops = @($deployment.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.ForEachStatementAst] -and
        $node.Variable.VariablePath.UserPath -eq 'file'
}, $true))
if ($fileLoops.Count -ne 2) { throw 'Expected explicit upload and live-probe file lists.' }
$uploaded = @($fileLoops[0].Condition.SafeGetValue() | Sort-Object)
$probed = @($fileLoops[1].Condition.SafeGetValue() | Sort-Object)
if (($uploaded -join ',') -ne ($expected -join ',') -or
    ($probed -join ',') -ne (($expected | Where-Object { $_ -ne 'staticwebapp.config.json' }) -join ',')) {
    throw 'The deployment upload/probe lists do not cover the complete website.'
}
$probe = $deployment.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Test-PortalFile'
}, $true)
if ($null -eq $probe) { throw 'The exact-content deployment probe is missing.' }
. ([scriptblock]::Create($probe.Extent.Text))
$path = Join-Path $site 'index.html'
$stream = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($path))
try {
    $response = [pscustomobject]@{ StatusCode = 200; RawContentStream = $stream }
    if (-not (Test-PortalFile -Response $response -Path $path) -or
        -not (Test-PortalFile -Response $response -Path $path)) {
        throw 'The live probe must accept matching bytes and rewind an already-read stream.'
    }
    if (Test-PortalFile -Response $response -Path (Join-Path $site 'ms2-world.webp')) {
        throw 'The live probe accepted stale or incorrect content with HTTP 200.'
    }
    $response.StatusCode = 404
    if (Test-PortalFile -Response $response -Path $path) {
        throw 'The live probe accepted a non-successful HTTP response.'
    }
} finally {
    $stream.Dispose()
}
Write-Host 'Pilot portal content and deployment checks passed without cloud access.'
