targetScope = 'subscription'

@description('Region for the isolated Maple2 foundation. The existing MapleTime group is not modified.')
param location string = 'brazilsouth'

var resourceGroupName = 'rg-maple2-${location}'
var tags = {
  project: 'maple2'
  brand: 'mapletime'
  environment: 'private'
  managedBy: 'bicep'
  sourceRepository: 'https://github.com/gugarosa/Maple2'
}

resource maple2Group 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module resources './resources.bicep' = {
  name: 'maple2-foundation-resources'
  scope: maple2Group
  params: {
    tags: tags
  }
}

output resourceGroup string = maple2Group.name
output virtualNetworkId string = resources.outputs.virtualNetworkId
output subnetId string = resources.outputs.subnetId
