targetScope = 'resourceGroup'

param tags object

var location = resourceGroup().location
var virtualNetworkName = 'vnet-maple2-${location}'
var subnetName = 'snet-maple2'

resource networkSecurityGroup 'Microsoft.Network/networkSecurityGroups@2024-05-01' = {
  name: 'nsg-maple2'
  location: location
  tags: tags
  properties: {
    securityRules: []
  }
}

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: virtualNetworkName
  location: location
  tags: tags
  properties: {
    privateEndpointVNetPolicies: 'Disabled'
    addressSpace: {
      addressPrefixes: [
        '10.43.0.0/16'
      ]
    }
    subnets: [
      {
        name: subnetName
        properties: {
          addressPrefix: '10.43.1.0/24'
          defaultOutboundAccess: false
          networkSecurityGroup: {
            id: networkSecurityGroup.id
          }
        }
      }
    ]
  }
}

output virtualNetworkId string = virtualNetwork.id
output subnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', virtualNetwork.name, subnetName)
