#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$DotNet = (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'),
    [string]$OutputDirectory,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath $DotNet -PathType Leaf)) {
    throw 'Select an installed .NET 8 SDK with -DotNet. No SDK or framework will be installed by this harness.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root 'tools\WebsitePreview\bin\fixtures'
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $root $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Fixture output must remain inside this worktree.'
}
$originalRoot = $env:DOTNET_ROOT
$originalPath = $env:PATH
Push-Location $root
try {
    $env:DOTNET_ROOT = Split-Path -Parent $DotNet
    $env:PATH = $env:DOTNET_ROOT + [IO.Path]::PathSeparator + $env:PATH
    $version = & $DotNet --version
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^8\.0\.') {
        throw 'The repository-selected .NET 8 SDK is required. global.json must not be bypassed.'
    }
    $project = Join-Path $root 'tools\WebsitePreview\WebsitePreview.csproj'
    & $DotNet build $project --configuration $Configuration --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'The account fixture/Web build failed.' }
    & $DotNet run --project $project --configuration $Configuration --no-build --no-restore -- --root $root --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'The compiled account UI regression checks failed.' }
} finally {
    Pop-Location
    $env:DOTNET_ROOT = $originalRoot
    $env:PATH = $originalPath
}
