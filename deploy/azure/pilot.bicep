targetScope = 'resourceGroup'

// Deploy incrementally to rg-maple2-brazilsouth after verifying its foundation,
// BRL billing currency and budget eligibility. This slice never owns the network.

@description('Linux administrator. SSH remains blocked by the existing foundation NSG.')
param adminUsername string = 'azureuser'

@minLength(1)
@description('Administrator public SSH key; no password or private key is accepted.')
param adminSshPublicKey string

@minLength(3)
@secure()
@description('Explicit recipient for MS2 budget warnings and shutdown notifications.')
param alertEmail string

@minValue(1)
@description('Monthly budget in subscription billing currency. The wrapper MUST confirm BRL. Alerts are delayed, not a hard spending cap.')
param monthlyBudget int = 250

@description('First day of the budget period, yyyy-MM-dd. Preserve this date on updates.')
param budgetStartDate string

@description('Budget expiry, yyyy-MM-dd. Must cover the pilot; preserve or deliberately extend on updates.')
param budgetEndDate string

@minLength(36)
@maxLength(36)
@description('Object ID of the deploying user, granted Secrets Officer on the MS2 vault only.')
param deployerObjectId string

@sealed()
type existingDiskState = {
  @minLength(1)
  osDiskName: string
  @minValue(32)
  @maxValue(4095)
  osDiskSizeGB: int
  @minLength(1)
  dataDiskName: string
}

@description('REQUIRED on incremental VM updates: exact current OS disk name/size and LUN0 disk name, all in this MS2 group. Omits immutable customData and never redeclares/resizes the data disk.')
param existingVmDisks existingDiskState?

@description('Only for a NEW/replacement VM: attach this existing MS2-group data disk instead of creating one. Its filesystem is never formatted. Leave empty for a new 32-GiB disk.')
param existingDataDiskName string = ''

var location = 'brazilsouth'
var vmName = 'vm-maple2-brs'
var tags = {
  project: 'maple2'
  brand: 'mapletime'
  environment: 'private-pilot'
  managedBy: 'bicep'
  sourceRepository: 'https://github.com/gugarosa/Maple2'
}
var creatingVm = existingVmDisks == null
var preservedDataDiskName = existingVmDisks.?dataDiskName ?? existingDataDiskName
var createDataDisk = empty(preservedDataDiskName)
var attachedDataDiskId = createDataDisk
  ? newDataDisk.id
  : resourceId('Microsoft.Compute/disks', preservedDataDiskName)
var cloudInit = replace(loadTextContent('cloud-init.yaml'), '__MAPLE2_FORMAT_NEW_DISK__', createDataDisk ? 'true' : 'false')
var secretsUserRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var secretsOfficerRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
var blobContributorRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
  name: 'vnet-maple2-brazilsouth'
}

resource subnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' existing = {
  parent: virtualNetwork
  name: 'snet-maple2'
}

resource networkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2024-05-01' existing = {
  name: 'nsg-maple2'
}

resource staticWebApp 'Microsoft.Web/staticSites@2023-12-01' = {
  name: 'swa-maple2-${uniqueString(resourceGroup().id)}'
  location: 'eastus2'
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    allowConfigFileUpdates: true
    stagingEnvironmentPolicy: 'Disabled'
  }
}

// A group-scoped, deallocate-only role avoids a VM/budget dependency cycle.
// Its only assignable scope is the isolated MS2 group, never the subscription.
resource budgetStopRoleDefinition 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(resourceGroup().id, 'maple2-budget-deallocate')
  properties: {
    roleName: 'Maple2 budget deallocate ${uniqueString(resourceGroup().id)}'
    description: 'Read and deallocate VMs in the isolated Maple2 resource group; no start, write, delete or data access.'
    type: 'CustomRole'
    assignableScopes: [
      subscriptionResourceId('Microsoft.Resources/resourceGroups', 'rg-maple2-brazilsouth')
    ]
    permissions: [
      {
        actions: [
          'Microsoft.Compute/virtualMachines/read'
          'Microsoft.Compute/virtualMachines/deallocate/action'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
  }
}

resource budgetStopWorkflow 'Microsoft.Logic/workflows@2019-05-01' = {
  name: 'logic-maple2-budget-stop'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    state: 'Enabled'
    definition: {
      '$schema': 'https://schema.management.azure.com/providers/Microsoft.Logic/schemas/2016-06-01/workflowdefinition.json#'
      contentVersion: '1.0.0.0'
      parameters: {}
      triggers: {
        request: {
          type: 'Request'
          kind: 'Http'
          inputs: {
            schema: {}
          }
        }
      }
      actions: {
        deallocate_vm: {
          type: 'Http'
          inputs: {
            method: 'POST'
            uri: uri(environment().resourceManager, '${resourceId('Microsoft.Compute/virtualMachines', vmName)}/deallocate?api-version=2024-07-01')
            authentication: {
              type: 'ManagedServiceIdentity'
              audience: environment().authentication.audiences[0]
            }
          }
          runAfter: {}
        }
      }
      outputs: {}
    }
  }
}

resource budgetStopRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, budgetStopWorkflow.id, budgetStopRoleDefinition.id)
  properties: {
    principalId: budgetStopWorkflow.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', budgetStopRoleDefinition.name)
  }
  dependsOn: [
    budgetStopRoleDefinition
  ]
}

resource budgetActionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-maple2-budget-stop'
  location: 'global'
  tags: tags
  properties: {
    enabled: true
    groupShortName: 'maple2stop'
    logicAppReceivers: [
      {
        name: 'deallocate-maple2-vm'
        resourceId: budgetStopWorkflow.id
        callbackUrl: listCallbackUrl('${budgetStopWorkflow.id}/triggers/request', '2016-06-01').value
        useCommonAlertSchema: false
      }
    ]
  }
  dependsOn: [
    budgetStopRole
  ]
}

resource monthlyCostBudget 'Microsoft.Consumption/budgets@2023-05-01' = {
  name: 'budget-maple2-monthly'
  properties: {
    amount: monthlyBudget
    category: 'Cost'
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
      endDate: budgetEndDate
    }
    notifications: {
      Actual80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        thresholdType: 'Actual'
        contactEmails: [
          alertEmail
        ]
        contactGroups: []
        contactRoles: []
      }
      Actual100Stop: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Actual'
        contactEmails: [
          alertEmail
        ]
        contactGroups: [
          budgetActionGroup.id
        ]
        contactRoles: []
      }
      Forecast100: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        thresholdType: 'Forecasted'
        contactEmails: [
          alertEmail
        ]
        contactGroups: []
        contactRoles: []
      }
    }
  }
}

resource publicIp 'Microsoft.Network/publicIPAddresses@2024-05-01' = {
  name: 'pip-maple2-brs'
  location: location
  tags: tags
  sku: {
    name: 'Standard'
  }
  properties: {
    publicIPAllocationMethod: 'Static'
    publicIPAddressVersion: 'IPv4'
  }
  dependsOn: [
    monthlyCostBudget
  ]
}

resource networkInterface 'Microsoft.Network/networkInterfaces@2024-05-01' = {
  name: 'nic-maple2-brs'
  location: location
  tags: tags
  properties: {
    enableIPForwarding: false
    networkSecurityGroup: {
      id: networkSecurityGroup.id
    }
    ipConfigurations: [
      {
        name: 'ipconfig'
        properties: {
          privateIPAllocationMethod: 'Dynamic'
          subnet: {
            id: subnet.id
          }
          publicIPAddress: {
            id: publicIp.id
          }
        }
      }
    ]
  }
}

resource newDataDisk 'Microsoft.Compute/disks@2024-03-02' = if (createDataDisk) {
  name: 'disk-maple2-brs-data'
  location: location
  tags: tags
  sku: {
    name: 'StandardSSD_LRS'
  }
  properties: {
    creationData: {
      createOption: 'Empty'
    }
    diskSizeGB: 32
    networkAccessPolicy: 'DenyAll'
    publicNetworkAccess: 'Disabled'
  }
  dependsOn: [
    monthlyCostBudget
  ]
}

resource vm 'Microsoft.Compute/virtualMachines@2024-07-01' = {
  name: vmName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    hardwareProfile: {
      vmSize: 'Standard_B2als_v2'
    }
    osProfile: union({
      computerName: vmName
      adminUsername: adminUsername
      linuxConfiguration: {
        disablePasswordAuthentication: true
        provisionVMAgent: true
        ssh: {
          publicKeys: [
            {
              keyData: adminSshPublicKey
              path: '/home/${adminUsername}/.ssh/authorized_keys'
            }
          ]
        }
      }
    }, creatingVm ? {
      customData: base64(cloudInit)
    } : {})
    storageProfile: {
      imageReference: {
        publisher: 'Canonical'
        offer: 'ubuntu-24_04-lts'
        sku: 'server'
        version: 'latest'
      }
      osDisk: {
        name: existingVmDisks.?osDiskName ?? 'disk-maple2-brs-os'
        createOption: 'FromImage'
        diskSizeGB: existingVmDisks.?osDiskSizeGB ?? 32
        deleteOption: 'Detach'
        managedDisk: {
          storageAccountType: 'StandardSSD_LRS'
        }
      }
      diskControllerType: 'SCSI'
      dataDisks: [
        {
          lun: 0
          name: createDataDisk ? newDataDisk.name : preservedDataDiskName
          createOption: 'Attach'
          deleteOption: 'Detach'
          caching: 'None'
          managedDisk: {
            id: attachedDataDiskId
          }
        }
      ]
    }
    networkProfile: {
      networkInterfaces: [
        {
          id: networkInterface.id
          properties: {
            deleteOption: 'Detach'
          }
        }
      ]
    }
    diagnosticsProfile: {
      bootDiagnostics: {
        enabled: true
      }
    }
  }
  dependsOn: [
    monthlyCostBudget
  ]
}

// Private blobs with Entra authentication, not a public download account.
// Authenticated HTTPS endpoints remain reachable without changing the subnet.
resource backupStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stmaple2${uniqueString(resourceGroup().id)}'
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
  }
  dependsOn: [
    monthlyCostBudget
  ]
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: backupStorage
  name: 'default'
}

resource backupContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'backups'
  properties: {
    publicAccess: 'None'
  }
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-maple2-${uniqueString(resourceGroup().id)}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: {
      family: 'A'
      name: 'standard'
    }
    accessPolicies: []
    enableRbacAuthorization: true
    enabledForDeployment: false
    enabledForDiskEncryption: false
    enabledForTemplateDeployment: false
    publicNetworkAccess: 'Enabled'
    softDeleteRetentionInDays: 7
  }
  dependsOn: [
    monthlyCostBudget
  ]
}

resource vmBackupRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(backupContainer.id, vm.id, blobContributorRoleId)
  scope: backupContainer
  properties: {
    principalId: vm.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobContributorRoleId
  }
}

resource vmSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, vm.id, secretsUserRoleId)
  scope: keyVault
  properties: {
    principalId: vm.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRoleId
  }
}

resource deployerSecretsRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, deployerObjectId, secretsOfficerRoleId)
  scope: keyVault
  properties: {
    principalId: deployerObjectId
    principalType: 'User'
    roleDefinitionId: secretsOfficerRoleId
  }
}

output resourceGroupName string = resourceGroup().name
output vmName string = vm.name
output vmId string = vm.id
output vmPrincipalId string = vm.identity.principalId
output publicIpName string = publicIp.name
output publicIpId string = publicIp.id
output publicIpAddress string = publicIp.properties.ipAddress
output staticWebAppName string = staticWebApp.name
output staticWebAppId string = staticWebApp.id
output staticWebAppDefaultHostname string = staticWebApp.properties.defaultHostname
output osDiskId string = vm.properties.storageProfile.osDisk.managedDisk.id
output dataDiskId string = attachedDataDiskId
output backupStorageAccountName string = backupStorage.name
output backupStorageAccountId string = backupStorage.id
output backupContainerName string = backupContainer.name
output backupContainerId string = backupContainer.id
output keyVaultName string = keyVault.name
output keyVaultId string = keyVault.id
output keyVaultUri string = keyVault.properties.vaultUri
output budgetName string = monthlyCostBudget.name
output budgetStopWorkflowName string = budgetStopWorkflow.name
output budgetStopWorkflowId string = budgetStopWorkflow.id
output budgetStopPrincipalId string = budgetStopWorkflow.identity.principalId
output budgetActionGroupId string = budgetActionGroup.id
