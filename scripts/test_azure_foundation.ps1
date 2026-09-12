#Requires -Version 5.1

<#
.SYNOPSIS
Check Azure foundation command safety without credentials or cloud changes.
#>
param([string]$CompiledTemplate)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deployment = Join-Path $root 'deploy\azure\deploy-foundation.ps1'
$tokens = $null
$errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile($deployment, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Azure deployment script does not parse.' }

$subscription = '11111111-2222-3333-4444-555555555555'
$state = @{
    Calls = [System.Collections.Generic.List[object]]::new()
    Exists = $false
    Owner = 'maple2'
    AccountId = $subscription
    Location = 'brazilsouth'
    Failure = ''
    Application = $false
}
$oldExitCode = Get-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue | Select-Object Value
function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Invoke-TestAzure {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    $global:LASTEXITCODE = 0
    $state.Calls.Add($Arguments)
    $index = [Array]::IndexOf($Arguments, '--subscription')
    Assert ($index -ge 0 -and $Arguments[$index + 1] -eq $subscription) 'Azure call did not pin the explicit subscription.'
    if ($Arguments[0] -eq $state.Failure) {
        $global:LASTEXITCODE = 19
        return
    }
    switch ("$($Arguments[0]) $($Arguments[1])") {
        'account show' { @{ id = $state.AccountId; state = 'Enabled' } | ConvertTo-Json }
        'group exists' { $state.Exists.ToString().ToLowerInvariant() }
        'group show' {
            $tags = @{ project = $state.Owner }
            if ($state.Application) { $tags.application = 'private-pilot' }
            @{ location = $state.Location; tags = $tags } | ConvertTo-Json
        }
        'deployment sub' { '{}' }
        default { throw 'Unexpected Azure operation.' }
    }
}
Set-Alias -Name az -Value Invoke-TestAzure -Scope Local
function Expect-Failure {
    param([scriptblock]$Action)
    $failed = $false
    try { & $Action *> $null } catch { $failed = $true }
    Assert $failed 'Unsafe or failed deployment must stop explicitly.'
}
try {
    & $deployment -SubscriptionId $subscription *> $null
    Assert ($state.Calls[-1][2] -eq 'what-if') 'Default operation must not create resources.'
    Assert ($state.Calls[-1] -contains (Join-Path $root 'deploy\azure\foundation.bicep')) 'Wrong template selected.'
    $state.Calls.Clear()
    & $deployment -SubscriptionId $subscription -Apply *> $null
    Assert ($state.Calls[-1][2] -eq 'create') 'Apply did not select deployment creation.'
    Assert (-not ($state.Calls | Where-Object { $_ -contains 'set' -or $_ -contains 'delete' })) 'Deployment changed global context or removed resources.'

    $state.Exists = $true
    $state.Owner = 'cosmic'
    $state.Calls.Clear()
    Expect-Failure { & $deployment -SubscriptionId $subscription -Apply }
    Assert (-not ($state.Calls | Where-Object { $_[0] -eq 'deployment' })) 'A non-Maple2 resource group was modified.'
    $state.Owner = 'maple2'
    $state.Location = 'eastus2'
    $state.Calls.Clear()
    Expect-Failure { & $deployment -SubscriptionId $subscription -Apply }
    Assert (-not ($state.Calls | Where-Object { $_[0] -eq 'deployment' })) 'A group in a different region was adopted.'
    $state.Location = 'brazilsouth'
    $state.Calls.Clear()
    & $deployment -SubscriptionId $subscription -Apply *> $null
    Assert ($state.Calls[-1][2] -eq 'create') 'An existing owned foundation could not be updated.'
    $state.Application = $true
    $state.Calls.Clear()
    Expect-Failure { & $deployment -SubscriptionId $subscription -Apply }
    Assert (-not ($state.Calls | Where-Object { $_[0] -eq 'deployment' })) 'Foundation application would erase active application ingress.'
    & $deployment -SubscriptionId $subscription *> $null
    Assert ($state.Calls[-1][2] -eq 'what-if') 'Application ownership must not block a read-only preview.'
    $state.Application = $false

    $state.AccountId = '00000000-0000-0000-0000-000000000000'
    $state.Calls.Clear()
    Expect-Failure { & $deployment -SubscriptionId $subscription -Apply }
    Assert ($state.Calls.Count -eq 1) 'Deployment continued with an unconfirmed subscription.'
    $state.AccountId = $subscription
    foreach ($command in @('account', 'group', 'deployment')) {
        $state.Failure = $command
        $state.Calls.Clear()
        Expect-Failure { & $deployment -SubscriptionId $subscription -Apply }
        Assert ($state.Calls[-1][0] -eq $command) 'Execution continued after an Azure failure.'
    }
    if ($CompiledTemplate) {
        $template = Get-Content -LiteralPath $CompiledTemplate -Raw -Encoding UTF8 | ConvertFrom-Json
        $resources = @($template.resources.PSObject.Properties.Value)
        if ($template.resources -is [Array]) { $resources = @($template.resources) }
        Assert ((@($resources.type | Sort-Object) -join ',') -eq 'Microsoft.Resources/deployments,Microsoft.Resources/resourceGroups') 'Unexpected subscription-scope resources.'
        $nested = @($resources | Where-Object type -eq 'Microsoft.Resources/deployments')[0].properties.template
        $children = @($nested.resources.PSObject.Properties.Value)
        if ($nested.resources -is [Array]) { $children = @($nested.resources) }
        Assert ((@($children.type | Sort-Object) -join ',') -eq 'Microsoft.Network/networkSecurityGroups,Microsoft.Network/virtualNetworks') 'Foundation includes unapproved DNS, compute or storage resources.'
        $nsg = @($children | Where-Object type -eq 'Microsoft.Network/networkSecurityGroups')[0]
        Assert (@($nsg.properties.securityRules).Count -eq 0) 'Foundation opens custom inbound access.'
        $network = @($children | Where-Object type -eq 'Microsoft.Network/virtualNetworks')[0]
        Assert ($network.properties.addressSpace.addressPrefixes[0] -eq '10.43.0.0/16') 'Maple2 network allocation changed.'
        Assert ($network.properties.subnets[0].properties.defaultOutboundAccess -eq $false) 'Implicit outbound access is enabled.'
    }
    Write-Host 'Azure foundation checks passed; no Azure login or resources were used.'
} finally {
    if ($oldExitCode) {
        $global:LASTEXITCODE = $oldExitCode.Value
    } else {
        Remove-Variable LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    }
}
