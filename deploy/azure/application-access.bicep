targetScope = 'resourceGroup'

@description('Existing MS2 storage account, not the MS1 assets account.')
param storageAccountName string
param vmPrincipalId string
param operatorObjectId string

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: storage
  name: 'default'
}
resource artifacts 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobs
  name: 'artifacts'
  properties: {
    publicAccess: 'None'
  }
}

var reader = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
var contributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource vmReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: artifacts
  name: guid(artifacts.id, vmPrincipalId, reader)
  properties: {
    principalId: vmPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: reader
  }
}
resource operatorWriter 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: artifacts
  name: guid(artifacts.id, operatorObjectId, contributor)
  properties: {
    principalId: operatorObjectId
    principalType: 'User'
    roleDefinitionId: contributor
  }
}

output artifactContainerId string = artifacts.id
