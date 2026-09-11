#Requires -Version 5.1

<#
.SYNOPSIS
Preview or provision the private Maple2 pilot with explicit subscription and budget boundaries.
.DESCRIPTION
Default is what-if. Apply creates the reviewed pilot resources but does not open
game/SSH/database ingress, import client data, deploy game services, or announce
public readiness. Validate bootstrap and deallocate a new VM after setup.
Existing VM/disk identity and budget periods are discovered and preserved.
.PARAMETER BillingCurrencyBudgetId
Optional existing budget resource ID in this subscription whose current-spend
unit confirms BRL when the Cost Management query API is throttled.
#>
param(
    [Parameter(Mandatory = $true)][Guid]$SubscriptionId,
    [Parameter(Mandatory = $true)][string]$AdminSshPublicKeyPath,
    [Parameter(Mandatory = $true)][string]$AlertEmail,
    [ValidateRange(1, 10000)][int]$MonthlyBudget = 250,
    [string]$ExistingDataDiskName = '',
    [string]$BillingCurrencyBudgetId = '',
    [switch]$Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$subscription = $SubscriptionId.ToString()
$group = 'rg-maple2-brazilsouth'
$scope = "/subscriptions/$subscription/resourceGroups/$group"
$vmName = 'vm-maple2-brs'
function Invoke-Azure {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)
    & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure command failed: $($Arguments[0]) $($Arguments[1]). See the CLI error above."
    }
}
function ParameterValue {
    param($Value)
    return @{ value = $Value }
}
if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw 'Azure CLI is required.'
}
if ([string]::IsNullOrWhiteSpace($AlertEmail) -or $AlertEmail -match '[\r\n]') {
    throw 'Supply an explicit budget notification email address.'
}
$null = [Net.Mail.MailAddress]::new($AlertEmail)
$publicKey = (Get-Content -LiteralPath $AdminSshPublicKeyPath -Raw -Encoding UTF8).Trim()
if ($publicKey -notmatch '^ssh-(ed25519|rsa) [A-Za-z0-9+/]+=*( [^\r\n]*)?$') {
    throw 'Supply a single OpenSSH public key, never a private key.'
}
$account = Invoke-Azure account show --subscription $subscription --only-show-errors --output json | ConvertFrom-Json
if ($account.id -ne $subscription -or $account.state -ne 'Enabled') {
    throw 'The explicit target subscription could not be confirmed.'
}
$defaultTenant = Invoke-Azure account show --only-show-errors --query tenantId --output tsv
if ($defaultTenant -ne $account.tenantId) {
    throw 'Sign in to the target tenant before resolving the operator identity; the CLI default will not be changed.'
}
$resourceGroup = Invoke-Azure group show --name $group --subscription $subscription --only-show-errors --output json | ConvertFrom-Json
if ($resourceGroup.location -ne 'brazilsouth' -or $resourceGroup.tags.project -ne 'maple2') {
    throw 'Apply the isolated Maple2 foundation first; refusing to adopt a different group.'
}
$network = Invoke-Azure network vnet show --name vnet-maple2-brazilsouth --resource-group $group `
    --subscription $subscription --only-show-errors --output json | ConvertFrom-Json
$subnet = @($network.subnets | Where-Object name -eq 'snet-maple2')
if (($network.addressSpace.addressPrefixes -join ',') -ne '10.43.0.0/16' -or $subnet.Count -ne 1 -or
    $subnet[0].addressPrefix -ne '10.43.1.0/24' -or
    $subnet[0].networkSecurityGroup.id -ne "$scope/providers/Microsoft.Network/networkSecurityGroups/nsg-maple2" -or
    $subnet[0].defaultOutboundAccess -ne $false) {
    throw 'The foundation network does not match the isolated private pilot contract.'
}
$nsg = Invoke-Azure network nsg show --name nsg-maple2 --resource-group $group --subscription $subscription `
    --only-show-errors --output json | ConvertFrom-Json
if (@($nsg.securityRules).Count -ne 0) {
    throw 'This bootstrap path requires a private NSG with no custom ingress rules.'
}
$userId = Invoke-Azure ad signed-in-user show --only-show-errors --query id --output tsv
if ($userId -notmatch '^[0-9a-fA-F-]{36}$') {
    throw 'A signed-in operator identity is required for the initial vault assignment.'
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('Maple2Pilot-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $temporary
try {
    if ($BillingCurrencyBudgetId) {
        $budgetPattern = '^/subscriptions/' + [regex]::Escape($subscription) +
            '/(?:resourceGroups/[^/?#]+/)?providers/Microsoft\.Consumption/budgets/[^/?#]+$'
        if ($BillingCurrencyBudgetId -notmatch $budgetPattern) {
            throw 'The currency-reference budget must belong to the explicit target subscription.'
        }
        $reference = Invoke-Azure rest --method get `
            --url "https://management.azure.com${BillingCurrencyBudgetId}?api-version=2023-05-01" `
            --only-show-errors --output json | ConvertFrom-Json
        if ($reference.id -ne $BillingCurrencyBudgetId -or $reference.properties.currentSpend.unit -ne 'BRL') {
            throw 'The existing budget does not confirm BRL subscription billing.'
        }
    } else {
        $queryFile = Join-Path $temporary 'cost-query.json'
        $query = @{
            type = 'ActualCost'
            timeframe = 'MonthToDate'
            dataset = @{
                granularity = 'None'
                aggregation = @{ totalCost = @{ name = 'PreTaxCost'; function = 'Sum' } }
            }
        } | ConvertTo-Json -Depth 10
        [IO.File]::WriteAllText($queryFile, $query, [Text.UTF8Encoding]::new($false))
        $cost = $null
        $queryError = Join-Path $temporary 'cost-query-error.txt'
        for ($attempt = 0; $attempt -lt 4; $attempt++) {
            $previousErrorAction = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $costJson = & az rest --method post `
                    --url "https://management.azure.com/subscriptions/$subscription/providers/Microsoft.CostManagement/query?api-version=2023-11-01" `
                    --body "@$queryFile" --only-show-errors --output json 2> $queryError
                $queryExit = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $previousErrorAction
            }
            if ($queryExit -eq 0) {
                $cost = $costJson | ConvertFrom-Json
                break
            }
            $failure = Get-Content -LiteralPath $queryError -Raw
            if ($failure -notmatch '429|Too Many Requests' -or $attempt -eq 3) {
                throw "Could not verify subscription billing currency: $failure"
            }
            Write-Host 'Azure Cost Management is throttling the read-only currency check; retrying in one minute.'
            Start-Sleep -Seconds 60
        }
        $currencyIndex = [Array]::IndexOf(@($cost.properties.columns | ForEach-Object name), 'Currency')
        if ($currencyIndex -lt 0 -or @($cost.properties.rows).Count -eq 0 -or
            @($cost.properties.rows | Where-Object { $_[$currencyIndex] -ne 'BRL' }).Count -ne 0) {
            throw 'BRL subscription billing could not be confirmed. Do not guess the budget currency.'
        }
    }
    $budgetList = Invoke-Azure rest --method get `
        --url "https://management.azure.com$scope/providers/Microsoft.Consumption/budgets?api-version=2023-05-01" `
        --only-show-errors --output json | ConvertFrom-Json
    $existingBudget = @($budgetList.value | Where-Object name -eq 'budget-maple2-monthly')
    if ($existingBudget.Count -gt 1) {
        throw 'Ambiguous pilot budget state.'
    }
    $start = [DateTime]::new([DateTime]::UtcNow.Year, [DateTime]::UtcNow.Month, 1)
    $end = $start.AddYears(5)
    if ($existingBudget.Count -eq 1) {
        $start = [DateTimeOffset]::Parse($existingBudget[0].properties.timePeriod.startDate).UtcDateTime
        $end = [DateTimeOffset]::Parse($existingBudget[0].properties.timePeriod.endDate).UtcDateTime
        if (-not $PSBoundParameters.ContainsKey('MonthlyBudget')) {
            $MonthlyBudget = [int]$existingBudget[0].properties.amount
        }
        if ($end -le [DateTime]::UtcNow) {
            throw 'The existing pilot budget expired; extend it deliberately before deployment.'
        }
    }
    $parameters = @{
        adminSshPublicKey = ParameterValue $publicKey
        alertEmail = ParameterValue $AlertEmail
        deployerObjectId = ParameterValue $userId
        monthlyBudget = ParameterValue $MonthlyBudget
        budgetStartDate = ParameterValue ($start.ToString('yyyy-MM-dd'))
        budgetEndDate = ParameterValue ($end.ToString('yyyy-MM-dd'))
    }
    $vms = @(Invoke-Azure vm list --resource-group $group --subscription $subscription --only-show-errors --output json | ConvertFrom-Json)
    $vm = @($vms | Where-Object name -eq $vmName)
    $disks = @(Invoke-Azure disk list --resource-group $group --subscription $subscription --only-show-errors --output json | ConvertFrom-Json)
    $diskPrefix = "$scope/providers/Microsoft.Compute/disks/"
    if ($vm.Count -eq 1) {
        if ($ExistingDataDiskName) {
            throw 'Do not replace the data disk through an existing-VM update.'
        }
        if ($vm[0].tags.project -ne 'maple2') {
            throw 'The existing pilot VM does not belong to Maple2.'
        }
        $osId = $vm[0].storageProfile.osDisk.managedDisk.id
        $data = @($vm[0].storageProfile.dataDisks)
        if (-not $osId.StartsWith($diskPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            $data.Count -ne 1 -or $data[0].lun -ne 0 -or
            -not $data[0].managedDisk.id.StartsWith($diskPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Existing VM disks are not the expected MS2-group OS and LUN0 pair.'
        }
        $osDisk = @($disks | Where-Object id -eq $osId)
        $dataDisk = @($disks | Where-Object id -eq $data[0].managedDisk.id)
        if ($osDisk.Count -ne 1 -or $dataDisk.Count -ne 1 -or
            $osDisk[0].sku.name -ne 'StandardSSD_LRS' -or $dataDisk[0].sku.name -ne 'StandardSSD_LRS') {
            throw 'Existing disk identity/tier could not be preserved by this pilot template.'
        }
        $parameters.adminUsername = ParameterValue $vm[0].osProfile.adminUsername
        $parameters.existingVmDisks = ParameterValue @{
            osDiskName = $osDisk[0].name
            osDiskSizeGB = [int]$osDisk[0].diskSizeGb
            dataDiskName = $dataDisk[0].name
        }
    } elseif ($vm.Count -ne 0) {
        throw 'Ambiguous pilot VM state.'
    } elseif ($ExistingDataDiskName) {
        $preserved = @($disks | Where-Object name -eq $ExistingDataDiskName)
        if ($preserved.Count -ne 1 -or $preserved[0].tags.project -ne 'maple2' -or
            $preserved[0].sku.name -ne 'StandardSSD_LRS' -or $preserved[0].managedBy) {
            throw 'The replacement VM must explicitly reuse an unattached, owned MS2 data disk.'
        }
        $parameters.existingDataDiskName = ParameterValue $ExistingDataDiskName
    } elseif (@($disks | Where-Object { $_.name -in @('disk-maple2-brs-data', 'disk-maple2-brs-os') }).Count -ne 0) {
        throw 'Pilot disks already exist without the VM. Inspect them and select preserved data explicitly; do not format them as new.'
    }
    $parameterFile = Join-Path $temporary 'pilot.parameters.json'
    $parameterJson = @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
       contentVersion = '1.0.0.0'
       parameters = $parameters } | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText($parameterFile, $parameterJson, [Text.UTF8Encoding]::new($false))
    Write-Host "Private pilot target: $group; budget BRL $MonthlyBudget/month. No public game ingress will be opened."
    $operation = 'what-if'
    if ($Apply) { $operation = 'create' }
    Invoke-Azure deployment group $operation --subscription $subscription --resource-group $group `
        --name maple2-private-pilot --mode Incremental `
        --template-file (Join-Path $PSScriptRoot 'pilot.bicep') --parameters "@$parameterFile" `
        --only-show-errors --output json
    if ($Apply) {
        Write-Host 'Validate cloud-init and the data mount, then deallocate a new pilot VM. DNS, HTTPS and game deployment remain separate gates.'
    }
} finally {
    Remove-Item -LiteralPath $temporary -Recurse -Force
}
