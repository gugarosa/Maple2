#Requires -Version 5.1

<#
.SYNOPSIS
Check the compiled private-pilot template and bootstrap without Azure access.
.EXAMPLE
az bicep build --file .\deploy\azure\pilot.bicep --outfile "$env:TEMP\maple2-pilot.json"
.\scripts\test_azure_pilot.ps1 -CompiledTemplate "$env:TEMP\maple2-pilot.json"
#>
param([Parameter(Mandatory = $true)][string]$CompiledTemplate)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
foreach ($name in @('deploy-pilot.ps1', 'deploy-portal.ps1')) {
    $tokens = $null
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $root "deploy\azure\$name"), [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw "Pilot deployment script does not parse: $name" }
}
$template = Get-Content -LiteralPath $CompiledTemplate -Raw -Encoding UTF8 | ConvertFrom-Json
$json = $template | ConvertTo-Json -Depth 100 -Compress
$cloud = (Get-Content -LiteralPath (Join-Path $root 'deploy\azure\cloud-init.yaml') -Raw -Encoding UTF8).Replace("`r`n", "`n")
$resources = @($template.resources.PSObject.Properties.Value)
if ($template.resources -is [Array]) { $resources = @($template.resources) }
$existingResources = @($resources | Where-Object { $_.PSObject.Properties['existing'] -and $_.existing })
$resources = @($resources | Where-Object { -not ($_.PSObject.Properties['existing'] -and $_.existing) })

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Resource {
    param([string]$Type)
    $matching = @($resources | Where-Object type -eq $Type)
    Assert ($matching.Count -eq 1) "Expected exactly one $Type resource."
    return $matching[0]
}
function Has-Dependency {
    param($Resource, [string]$Symbol, [string]$Type, [string]$Name)
    return $Resource.dependsOn -contains $Symbol -or
        $Resource.dependsOn -contains "[resourceId('$Type', '$Name')]"
}

$allowedTypes = @(
    'Microsoft.Authorization/roleAssignments'
    'Microsoft.Authorization/roleDefinitions'
    'Microsoft.Compute/disks'
    'Microsoft.Compute/virtualMachines'
    'Microsoft.Consumption/budgets'
    'Microsoft.Insights/actionGroups'
    'Microsoft.KeyVault/vaults'
    'Microsoft.Logic/workflows'
    'Microsoft.Network/networkInterfaces'
    'Microsoft.Network/publicIPAddresses'
    'Microsoft.Storage/storageAccounts'
    'Microsoft.Storage/storageAccounts/blobServices'
    'Microsoft.Storage/storageAccounts/blobServices/containers'
    'Microsoft.Web/staticSites'
)
Assert ($template.'$schema' -match '/deploymentTemplate.json#') 'Pilot must be resource-group scoped.'
Assert ($existingResources.Count -eq 3 -and (@($existingResources.type | Sort-Object) -join ',') -eq 'Microsoft.Network/networkSecurityGroups,Microsoft.Network/virtualNetworks,Microsoft.Network/virtualNetworks/subnets') 'Only the foundation network may be referenced as existing.'
Assert (@($existingResources | Where-Object { $_.PSObject.Properties['properties'] -or $_.PSObject.Properties['scope'] }).Count -eq 0) 'Existing network references must not change properties or access another group.'
Assert ($resources.Count -eq 17) 'Unexpected pilot resource count.'
Assert ((@($resources.type | Sort-Object -Unique) -join ',') -eq ($allowedTypes -join ',')) 'Pilot includes an unapproved resource type, such as DNS, NSG rules, extensions or a registry.'
Assert ($json -notmatch 'cosmic|stmapletimeassets|10\.42\.|repositoryToken|repositoryUrl|adminPassword|securityRules|inboundNatRules') 'Pilot references unrelated resources, credentials, repository linking or public ingress.'

$site = Resource 'Microsoft.Web/staticSites'
Assert ($site.location -eq 'eastus2' -and $site.sku.name -eq 'Free' -and $site.sku.tier -eq 'Free') 'Static Web App must remain Free in eastus2.'
Assert ($site.name -match 'swa-maple2-' -and $site.name -match 'uniqueString\(resourceGroup\(\).id\)') 'Site name must be deterministic and globally unique.'
$ip = Resource 'Microsoft.Network/publicIPAddresses'
Assert ($ip.name -eq 'pip-maple2-brs' -and $ip.sku.name -eq 'Standard') 'Unexpected public IP name or tier.'
Assert ($ip.properties.publicIPAllocationMethod -eq 'Static' -and $ip.properties.publicIPAddressVersion -eq 'IPv4') 'Explicit outbound requires a static Standard IPv4.'
$nic = Resource 'Microsoft.Network/networkInterfaces'
Assert ($nic.properties.enableIPForwarding -eq $false) 'IP forwarding must remain disabled.'
Assert ($nic.properties.networkSecurityGroup.id -match "'nsg-maple2'") 'NIC must use the existing foundation NSG.'
Assert ($nic.properties.ipConfigurations.Count -eq 1) 'Unexpected additional network interfaces.'
Assert ($nic.properties.ipConfigurations[0].properties.subnet.id -match "'vnet-maple2-brazilsouth', 'snet-maple2'") 'NIC must reference the existing MS2 subnet.'
Assert ($nic.properties.ipConfigurations[0].properties.publicIPAddress.id -match 'pip-maple2-brs') 'NIC must use the dedicated MS2 outbound IP.'

$vm = Resource 'Microsoft.Compute/virtualMachines'
Assert ($vm.location -eq 'brazilsouth' -or $vm.location -eq "[variables('location')]") 'VM region changed.'
Assert ($template.variables.vmName -eq 'vm-maple2-brs') 'VM identity must remain MS2-only.'
Assert ($vm.properties.hardwareProfile.vmSize -eq 'Standard_B2als_v2') 'Pilot must use the bounded 2-vCPU/4-GiB VM.'
Assert ($vm.identity.type -eq 'SystemAssigned') 'VM needs its own managed identity.'
$osProfile = $vm.properties.osProfile | ConvertTo-Json -Depth 20 -Compress
Assert ($osProfile -match 'disablePasswordAuthentication.+true' -and $osProfile -match 'adminSshPublicKey') 'Only the supplied public SSH key may configure access.'
Assert ($osProfile -match 'creatingVm' -and $osProfile -match 'customData') 'Existing VM updates must omit immutable customData.'
Assert ($template.variables.creatingVm -eq "[equals(parameters('existingVmDisks'), null())]") 'Existing VM detection changed.'
Assert ($template.parameters.existingVmDisks.nullable -eq $true -and $template.parameters.existingVmDisks.'$ref' -eq '#/definitions/existingDiskState') 'Existing VM disk state must use the explicit typed contract.'
$diskContract = $template.definitions.existingDiskState
Assert ($diskContract.additionalProperties -eq $false -and (@($diskContract.properties.PSObject.Properties.Name | Sort-Object) -join ',') -eq 'dataDiskName,osDiskName,osDiskSizeGB') 'Existing VM state must require all three disk fields.'
Assert (@($diskContract.properties.PSObject.Properties.Value | Where-Object { $_.PSObject.Properties['nullable'] -and $_.nullable }).Count -eq 0) 'Existing disk names/sizes must not silently default when state is supplied.'
$image = $vm.properties.storageProfile.imageReference
Assert ($image.publisher -eq 'Canonical' -and $image.offer -eq 'ubuntu-24_04-lts' -and $image.sku -eq 'server') 'VM must use Ubuntu 24.04 x64.'
Assert ($vm.properties.storageProfile.diskControllerType -eq 'SCSI') 'Bootstrap requires the Azure SCSI LUN0 path.'
$osDisk = $vm.properties.storageProfile.osDisk
Assert ($osDisk.name -match 'existingVmDisks.+osDiskName' -and $osDisk.diskSizeGB -match 'existingVmDisks.+osDiskSizeGB.+32') 'OS disk name and size must be preserved on VM updates.'
Assert ($osDisk.deleteOption -eq 'Detach' -and $osDisk.managedDisk.storageAccountType -eq 'StandardSSD_LRS') 'OS disk must be retained and use Standard SSD.'
$data = Resource 'Microsoft.Compute/disks'
Assert ($data.condition -eq "[variables('createDataDisk')]" -and $template.variables.createDataDisk -eq "[empty(variables('preservedDataDiskName'))]") 'Only explicitly new data disks may be created.'
Assert ($template.variables.preservedDataDiskName -match 'existingVmDisks.+dataDiskName.+existingDataDiskName') 'VM updates and replacement-VM disk reuse must preserve the data disk.'
Assert ($data.properties.diskSizeGB -eq 32 -and $data.sku.name -eq 'StandardSSD_LRS' -and $data.properties.creationData.createOption -eq 'Empty') 'New data disk must be a separate 32-GiB Standard SSD.'
Assert ($data.properties.networkAccessPolicy -eq 'DenyAll' -and $data.properties.publicNetworkAccess -eq 'Disabled') 'Data disk export must not be public.'
$attachedDisks = @($vm.properties.storageProfile.dataDisks)
Assert ($attachedDisks.Count -eq 1 -and $attachedDisks[0].lun -eq 0 -and $attachedDisks[0].createOption -eq 'Attach' -and $attachedDisks[0].deleteOption -eq 'Detach') 'LUN0 must attach and preserve its managed disk.'
Assert (-not $attachedDisks[0].PSObject.Properties['diskSizeGB']) 'Attaching an existing data disk must not resize it.'
Assert ($template.variables.attachedDataDiskId -match "resourceId\('Microsoft.Compute/disks', variables\('preservedDataDiskName'\)\)") 'Existing data disk IDs must stay in the deployment group.'

$storage = Resource 'Microsoft.Storage/storageAccounts'
Assert ($storage.kind -eq 'StorageV2' -and $storage.sku.name -eq 'Standard_LRS') 'Unexpected backup storage kind or tier.'
Assert ($storage.properties.allowBlobPublicAccess -eq $false -and $storage.properties.allowSharedKeyAccess -eq $false) 'Backups must not permit public blobs or shared-key access.'
Assert ($storage.properties.supportsHttpsTrafficOnly -eq $true -and $storage.properties.minimumTlsVersion -eq 'TLS1_2') 'Backups require HTTPS and TLS 1.2.'
$container = Resource 'Microsoft.Storage/storageAccounts/blobServices/containers'
Assert ($container.name -match "'default', 'backups'" -and $container.properties.publicAccess -eq 'None') 'Only the private backups container is allowed.'
$vault = Resource 'Microsoft.KeyVault/vaults'
Assert ($vault.properties.enableRbacAuthorization -eq $true -and @($vault.properties.accessPolicies).Count -eq 0) 'Vault must use RBAC without legacy access policies.'
$roles = @($resources | Where-Object type -eq 'Microsoft.Authorization/roleAssignments')
Assert ($roles.Count -eq 4) 'Only shutdown, backup and two vault role assignments are permitted.'
$backupRole = @($roles | Where-Object { $_.properties.roleDefinitionId -match 'blobContributorRoleId' })
Assert ($backupRole.Count -eq 1 -and $backupRole[0].scope -match 'Microsoft.Storage/storageAccounts/blobServices/containers' -and $backupRole[0].scope -match "'default', 'backups'") 'VM backup permissions must be container-scoped.'
$vaultRoles = @($roles | Where-Object { $_.properties.roleDefinitionId -match 'secrets(User|Officer)RoleId' })
Assert ($vaultRoles.Count -eq 2 -and @($vaultRoles | Where-Object { $_.scope -notmatch 'Microsoft.KeyVault/vaults' }).Count -eq 0) 'Secrets User/Officer must be scoped only to this vault.'
Assert ($template.variables.secretsUserRoleId -match '4633458b-17de-408a-b874-0445c86b69e6' -and $template.variables.secretsOfficerRoleId -match 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7') 'Unexpected vault permissions.'

$stopDefinition = Resource 'Microsoft.Authorization/roleDefinitions'
Assert ($stopDefinition.properties.assignableScopes.Count -eq 1 -and $stopDefinition.properties.assignableScopes[0] -eq "[subscriptionResourceId('Microsoft.Resources/resourceGroups', 'rg-maple2-brazilsouth')]") 'Shutdown permissions must be assignable only in the fixed MS2 group.'
$permissions = $stopDefinition.properties.permissions
Assert ($permissions.Count -eq 1 -and (@($permissions[0].actions | Sort-Object) -join ',') -eq 'Microsoft.Compute/virtualMachines/deallocate/action,Microsoft.Compute/virtualMachines/read') 'Shutdown role must only read and deallocate; no wildcard, start, write or delete.'
Assert (@($permissions[0].dataActions).Count -eq 0) 'Shutdown identity must have no data permissions.'
$stopRoles = @($roles | Where-Object { $_.properties.roleDefinitionId -match 'maple2-budget-deallocate' })
Assert ($stopRoles.Count -eq 1 -and -not $stopRoles[0].PSObject.Properties['scope'] -and $stopRoles[0].properties.principalId -match "'budgetStopWorkflow'") 'The shutdown identity alone must receive the custom role at MS2 group scope.'
Assert ($stopRoles[0].properties.roleDefinitionId -match "^\[subscriptionResourceId\('Microsoft.Authorization/roleDefinitions'") 'Use the canonical subscription role-definition ID so repeat deployments do not attempt an immutable assignment change.'
$workflow = Resource 'Microsoft.Logic/workflows'
Assert ($workflow.identity.type -eq 'SystemAssigned' -and $workflow.properties.state -eq 'Enabled') 'Shutdown workflow needs its own enabled managed identity.'
$actions = @($workflow.properties.definition.actions.PSObject.Properties)
Assert ($actions.Count -eq 1 -and $actions[0].Name -eq 'deallocate_vm') 'Shutdown workflow must have exactly one deallocation action.'
$stop = $actions[0].Value.inputs
Assert ($stop.method -eq 'POST' -and $stop.uri -match "resourceId\('Microsoft.Compute/virtualMachines', variables\('vmName'\)\)" -and $stop.uri -match '/deallocate\?api-version=' -and $stop.authentication.type -eq 'ManagedServiceIdentity') 'Shutdown must target only the MS2 VM using managed identity.'
$actionGroup = Resource 'Microsoft.Insights/actionGroups'
Assert ($actionGroup.properties.enabled -eq $true -and $actionGroup.properties.logicAppReceivers.Count -eq 1) 'Budget action group must invoke its shutdown workflow.'
Assert ($actionGroup.properties.logicAppReceivers[0].resourceId -match 'logic-maple2-budget-stop') 'Budget action group targets the wrong workflow.'
Assert (($actionGroup.dependsOn -join ',') -match 'budgetStopRole|Microsoft.Authorization/roleAssignments') 'Shutdown RBAC must be provisioned before the action group.'
$budget = Resource 'Microsoft.Consumption/budgets'
Assert ($budget.name -eq 'budget-maple2-monthly' -and $budget.properties.timeGrain -eq 'Monthly' -and $budget.properties.category -eq 'Cost') 'Budget must be MS2-only and monthly.'
Assert (-not $budget.PSObject.Properties['scope']) 'Budget must remain at the dedicated resource-group scope.'
Assert ($template.parameters.monthlyBudget.defaultValue -eq 250 -and $budget.properties.amount -eq "[parameters('monthlyBudget')]") 'Default budget must be 250 in the verified BRL billing currency.'
foreach ($notification in $budget.properties.notifications.PSObject.Properties.Value) {
    Assert ($notification.enabled -eq $true -and $notification.contactEmails.Count -eq 1 -and $notification.contactEmails[0] -eq "[parameters('alertEmail')]") 'Every budget notification must use the explicit alert email.'
}
$stopNotice = $budget.properties.notifications.Actual100Stop
Assert ($stopNotice.threshold -eq 100 -and $stopNotice.thresholdType -eq 'Actual' -and $stopNotice.contactGroups.Count -eq 1 -and $stopNotice.contactGroups[0] -match 'ag-maple2-budget-stop') '100% actual spend must invoke the deallocation action group.'
foreach ($guarded in @($vm, $ip, $data, $storage, $vault)) {
    Assert (Has-Dependency $guarded 'monthlyCostBudget' 'Microsoft.Consumption/budgets' 'budget-maple2-monthly') 'Billable pilot resources must wait for a successfully provisioned budget guard.'
}

$embeddedCloud = @($template.variables.PSObject.Properties.Value | Where-Object { $_ -is [string] -and $_.StartsWith('#cloud-config') })
Assert ($embeddedCloud.Count -eq 1 -and $embeddedCloud[0].Replace("`r`n", "`n") -eq $cloud) 'Compiled customData must contain the current cloud-init file.'
Assert ($template.variables.cloudInit -match "__MAPLE2_FORMAT_NEW_DISK__.+createDataDisk.+true.+false") 'Formatting permission must be enabled only for a new data disk.'
Assert ($cloud -match 'set -Eeuo pipefail' -and $cloud -match "trap '.+bootstrap failed.+ERR") 'Bootstrap failures must be explicit.'
Assert ($cloud -match 'readlink -f /dev/disk/azure/scsi1/lun0' -and $cloud -match 'foreign_mounts.+') 'Bootstrap must use only the unoccupied Azure LUN0 disk.'
Assert ($cloud -match 'RequiresMountsFor=/srv/maple2' -and $cloud -match 'ExecStartPre=/usr/bin/mountpoint --quiet /srv/maple2') 'Docker must fail if its persistent mount is unavailable.'
Assert ($cloud -match '"data-root": "/srv/maple2/docker"' -and $cloud -match "printf 'UUID=%s %s %s defaults 0 %s") 'Docker data must use a persistent UUID mount without nofail.'
Assert ($cloud -notmatch 'nofail|disk_setup:|fs_setup:|mkfs\.ext4\s+-F|wipefs\s+(-a|--all)|curl.+\|\s*(ba)?sh|git\s+clone|docker\s+compose\s+up') 'Bootstrap includes destructive disk setup, unsafe fallback or application deployment.'
$formatBranch = [regex]::Match($cloud, '(?s)case "\$filesystem_count" in\s+0\)(.*?)\s+1\)')
Assert $formatBranch.Success 'Expected explicit new-disk formatting branch.'
Assert ($formatBranch.Groups[1].Value -match '(?s)allow_format.+true.+partitioned disk.+wipefs --no-act --json.+signatures.+blockdev --getsize64.+cmp --bytes="\$disk_bytes" "\$disk" /dev/zero.+mkfs.ext4 -L maple2data') 'Formatting requires explicit permission, no partitions/signatures and a full zero/readability check.'
Assert ([regex]::Matches($cloud, 'mkfs\.').Count -eq 1) 'Only the guarded format operation is allowed.'
Assert ($cloud -match '(?s)mountpoint --quiet "\$mount_dir".+Mounted UUID differs.+apt-get install --yes --no-install-recommends docker.io docker-compose-v2.+systemctl enable --now docker.service') 'Native Docker installation/start must follow verified data mounting.'
Assert ($cloud -match "(?s)packages:\s+- curl\s+- git\s+- jq\s+write_files:") 'Only curl/git/jq may install before the data mount.'

$requiredOutputs = @(
    'vmName', 'vmId', 'vmPrincipalId', 'publicIpName', 'publicIpAddress',
    'staticWebAppName', 'staticWebAppDefaultHostname', 'osDiskId', 'dataDiskId',
    'backupStorageAccountName', 'backupStorageAccountId', 'backupContainerName',
    'keyVaultName', 'keyVaultId', 'budgetStopWorkflowId', 'budgetStopPrincipalId'
)
foreach ($name in $requiredOutputs) {
    Assert ([bool]$template.outputs.PSObject.Properties[$name]) "Missing verification output: $name"
}
$outputs = $template.outputs | ConvertTo-Json -Depth 30 -Compress
Assert ($outputs -notmatch 'listKeys|listSecrets|listCallbackUrl|callbackUrl|password|token|customData') 'Outputs must never expose secrets or signed workflow callbacks.'
Write-Host 'Azure pilot checks passed; template and bootstrap inspected without Azure access or service changes.'
