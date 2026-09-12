#Requires -Version 5.1

<#
.SYNOPSIS
Preview or provision Maple2's isolated Azure resource group and private network.
.DESCRIPTION
Preview is the default. Apply creates no VM, disks, public IP, registry, database,
or public application endpoint. DNS remains at Porkbun.
The existing MapleTime resource group and DNS records are not modified.
.PARAMETER SubscriptionId
Explicit target subscription; the command never changes the CLI default subscription.
.PARAMETER Apply
Provision the reviewed foundation instead of running Azure what-if.
#>
param(
    [Parameter(Mandatory = $true)][Guid]$SubscriptionId,
    [ValidatePattern('^[a-z][a-z0-9]+$')][string]$Location = 'brazilsouth',
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required. Sign in with an identity authorized for the target subscription.'
}

function Invoke-Azure {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure command failed: $($Arguments[0]) $($Arguments[1]). See the CLI error above."
    }
}

$subscription = $SubscriptionId.ToString()
$group = "rg-maple2-$Location"
$account = Invoke-Azure account show --subscription $subscription --only-show-errors --output json | ConvertFrom-Json
if ($account.id -ne $subscription -or $account.state -ne 'Enabled') {
    throw 'The explicit subscription is not enabled or could not be confirmed.'
}
$exists = Invoke-Azure group exists --name $group --subscription $subscription --only-show-errors --output tsv
if ($exists -eq 'true') {
    $existing = Invoke-Azure group show --name $group --subscription $subscription --only-show-errors --output json | ConvertFrom-Json
    if ($existing.location -ne $Location -or $null -eq $existing.tags -or
        -not $existing.tags.PSObject.Properties['project'] -or $existing.tags.project -ne 'maple2') {
        throw "Refusing to adopt a resource group not identified as this Maple2 foundation: $group"
    }
    if ($Apply -and $existing.tags.PSObject.Properties['application']) {
        throw 'An application deployment owns this group''s ingress. Use the application procedure; reapplying the empty foundation would remove its access rules.'
    }
} elseif ($exists -ne 'false') {
    throw 'Azure returned an unexpected resource-group existence result.'
}

$operation = 'what-if'
if ($Apply) { $operation = 'create' }
Invoke-Azure deployment sub $operation `
    --subscription $subscription `
    --location $Location `
    --name "maple2-foundation-$Location" `
    --template-file (Join-Path $PSScriptRoot 'foundation.bicep') `
    --parameters "location=$Location" `
    --only-show-errors `
    --output json
if ($Apply) {
    Write-Host 'Foundation provisioned. Porkbun records, HTTPS, compute and game deployment are separate steps.'
} else {
    Write-Host 'Preview only. Review the resource changes before rerunning with -Apply.'
}
