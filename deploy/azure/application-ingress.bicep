targetScope = 'resourceGroup'

@description('The explicitly approved workstation IPv4 /32. Never use an Internet-wide prefix.')
param clientCidr string

resource nsg 'Microsoft.Network/networkSecurityGroups@2024-05-01' existing = {
  name: 'nsg-maple2'
}
resource certificateValidation 'Microsoft.Network/networkSecurityGroups/securityRules@2024-05-01' = {
  parent: nsg
  name: 'allow-ms2-certificate-validation'
  properties: {
    description: 'ACME HTTP-01 only; the proxy rejects ordinary HTTP requests on port 80.'
    priority: 100
    direction: 'Inbound'
    access: 'Allow'
    protocol: 'Tcp'
    sourceAddressPrefix: 'Internet'
    sourcePortRange: '*'
    destinationAddressPrefix: '*'
    destinationPortRange: '80'
  }
}
resource privatePilot 'Microsoft.Network/networkSecurityGroups/securityRules@2024-05-01' = {
  parent: nsg
  name: 'allow-ms2-private-pilot'
  properties: {
    description: 'Approved pilot workstation: HTTPS accounts, native game and native HTTP assets.'
    priority: 110
    direction: 'Inbound'
    access: 'Allow'
    protocol: 'Tcp'
    sourceAddressPrefix: clientCidr
    sourcePortRange: '*'
    destinationAddressPrefix: '*'
    destinationPortRanges: [
      '443'
      '4000'
      '20001-20003'
    ]
  }
}
