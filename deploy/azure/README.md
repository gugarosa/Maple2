# Maple2 Azure foundation

This is an isolated starting point for MapleStory2 under the MapleTime brand.
It is **not an Azure game-server deployment** and does not migrate the running
MapleTime/MapleStory 1 service.

## Provisioned scope

Verified on 2026-09-11 in the selected Visual Studio Enterprise subscription:

| Resource | Name / allocation |
|---|---|
| Dedicated resource group | `rg-maple2-brazilsouth` |
| Region | Brazil South, matching the existing MapleTime VM region |
| Virtual network | `vnet-maple2-brazilsouth`, `10.43.0.0/16` |
| Subnet | `snet-maple2`, `10.43.1.0/24` |
| Network security group | `nsg-maple2`, no custom inbound allow rules |
| Public DNS child zone | `ms2.mapletime.dev` |

The subnet has implicit default outbound access disabled. It has no peering with
the existing MapleTime network (`10.42.0.0/16`), no connected workloads, and no
public ingress opened by this template.

**No VM, disks, public IP, registry, managed database, website, secrets, or role
assignments are created.** Public Azure DNS zones and queries are metered; the
foundation is not a free game-hosting deployment. Check current
[DNS pricing](https://azure.microsoft.com/pricing/details/dns/) and subscription
billing currency before extending it. Set an MS2-specific budget and safeguards
before provisioning compute or public assets; do not reuse the Cosmic shutdown
automation against a different workload.

The public Azure retail API returned standard first-tier list prices of about
USD 0.50 per public zone-month and USD 0.40 per million queries on 2026-09-11.
These are reference prices, not a BRL subscription bill or a hosting estimate.

The existing `rg-cosmic-brazilsouth` resources, VM, portal, database, DNS bindings,
email configuration, and deployment identities remain unchanged.

## Preview and apply

Use an explicit subscription ID. The script never changes Azure CLI's default
subscription and refuses to adopt an existing group with the wrong project tag
or location.

```powershell
az account list --query "[].{Name:name,Id:id}" --output table
$subscription = "<the intended subscription ID>"

# No resource changes: inspect Azure what-if first.
.\deploy\azure\deploy-foundation.ps1 -SubscriptionId $subscription

# Apply only the reviewed foundation.
.\deploy\azure\deploy-foundation.ps1 -SubscriptionId $subscription -Apply
```

`foundation.bicep` creates the dedicated group at subscription scope.
`resources.bicep` owns only the network, NSG, and child DNS zone in that group.
The resources carry `project=maple2`, `brand=mapletime`,
`environment=foundation`, and repository/management tags.

Review changes before every apply. Routine updates must not delete/recreate the
DNS zone, change its authoritative servers, or replace data-bearing resources
when those are introduced later.

## Domain structure and current boundaries

| Hostname | Role / current state |
|---|---|
| `mapletime.dev` | Existing MapleTime portal; unchanged |
| `www.mapletime.dev` | Existing portal alias; unchanged |
| `ms.mapletime.dev` | Proposed MapleStory 1 portal name; not published by this foundation |
| `ms2.mapletime.dev` | Maple2 DNS child zone prepared in Azure; parent delegation and hosting pending |

The domain is registered and authoritative DNS is hosted at **Porkbun**, not
Azure DNS. Creating the child zone in Azure does not automatically publish it.
Keep the parent domain on its current Porkbun nameservers.

The existing MapleTime Static Web App is Free tier and already has both custom
hostname slots bound (`mapletime.dev` and `www.mapletime.dev`). A simple CNAME
does not add a supported third HTTPS hostname. Publishing `ms.mapletime.dev`
requires a separate deliberate hostname/plan decision, including canonical URL,
OAuth callback, cookie, and redirect behavior. Do not remove either current
binding as a side effect of Maple2 work.

The existing portal address and MapleTime VM address are different. Do not point
a game launcher at a Static Web App frontend or copy that frontend IP into
`GAME_IP`.

### Delegate only `ms2` at Porkbun

Retrieve the current Azure-assigned servers rather than assuming an old value:

```powershell
az network dns zone show `
    --subscription $subscription `
    --resource-group rg-maple2-brazilsouth `
    --name ms2.mapletime.dev `
    --query nameServers --output tsv
```

The verified values for the initial zone are:

| Porkbun record type | Host | Answer |
|---|---|---|
| `NS` | `ms2` | `ns1-06.azure-dns.com` |
| `NS` | `ms2` | `ns2-06.azure-dns.net` |
| `NS` | `ms2` | `ns3-06.azure-dns.org` |
| `NS` | `ms2` | `ns4-06.azure-dns.info` |

Add these as records in the **parent `mapletime.dev` zone**, using a reasonable
TTL such as 600 seconds. This delegates only the child name and its descendants;
it does not replace the registrar's nameserver settings for the whole domain.
Do not modify the existing apex/www, mail-verification, SPF, or DKIM records.

No Porkbun credentials were available to this setup, so these parent records
have **not** been applied. They require registrar access. After delegation:

```powershell
Resolve-DnsName ms2.mapletime.dev -Type NS
Resolve-DnsName ms2.mapletime.dev -Type SOA -Server ns1-06.azure-dns.com
```

The Azure zone initially contains only NS/SOA records. No A/CNAME points to an
unprovisioned server, and no website is implied by successful delegation.
`ms2.mapletime.dev` is this child zone's apex: do not create a CNAME at `@`,
where NS/SOA already exist. Use the hosting provider's supported apex A/alias
and verification records, or put a CNAME on a child name such as `www`.

### Publishing the player-facing names

`.dev` is HSTS-preloaded: browsers require HTTPS. Select and validate the actual
web host/certificate before publishing a working portal address.

A VM serving both HTTPS and the native game ports can use the same hostname for
both roles. If the portal uses Static Web Apps or another HTTP-only frontend,
give the native game a separate name, such as `play.ms2.mapletime.dev`, pointing
to its own static public IPv4. An HTTP frontend cannot proxy arbitrary native
game TCP ports.

Maple2's Login/Game handoff currently advertises IPv4 literals. The launcher can
resolve a hostname, but the server's `LOGIN_IP` and `GAME_IP` must contain the
client-reachable public IP, not the Docker bind address or private VM address.
The Web/UGC endpoint requires its own routing validation; its current
`WEB_IP`/`WEB_PORT` creates an HTTP URL rather than a configurable HTTPS origin.

## Next deployment boundary

Keep the existing six-service local topology while preparing the pilot:

- MySQL and internal gRPC stay private. Do not publish 3306 or 21000-21003.
- Public game ports will be Login 20001, instanced Game 20002, and normal Game
  20003, opened only after a private bootstrap succeeds.
- Registration requires a real HTTPS endpoint and trusted reverse-proxy handling
  for rate limiting. Do not expose the local-only registration setup unchanged.
- Provision separate MS2 data, backups, deployment identity, and restore/rollback
  procedures. A later GitHub identity should be scoped to this group, not granted
  subscription-wide ownership or borrowed from Cosmic.
- Size compute from the MS2 workload, not the MapleTime VM defaults. The available
  complete metadata ingestion required an 8 GiB allowance; runtime and ingestion
  resource requirements are different.
- Keep original client/data distribution and the tested compatibility set within
  the boundaries in [CLIENT_SETUP.md](../../CLIENT_SETUP.md). Do not upload
  proprietary content into a public release by treating infrastructure creation
  as distribution approval.

The foundation intentionally leaves compute, registration/recovery hosting,
public ingress, DNS A/CNAME targets, TLS, and clean-PC multiplayer verification
for the next reviewed deployment. The local server continues to operate
independently.

## Validation without Azure access

```powershell
az bicep build --file .\deploy\azure\foundation.bicep `
    --outfile "$env:TEMP\maple2-foundation.json"
.\scripts\test_azure_foundation.ps1 -CompiledTemplate "$env:TEMP\maple2-foundation.json"
```

The test uses an Azure CLI stub for deployment safety and inspects the compiled
resource allowlist. It checks preview-only defaults, explicit subscription
selection, refusal to adopt other projects/regions, error propagation, private
network defaults, and the absence of compute/storage/public endpoint resources.
CI compiles and tests the foundation; it does not authenticate or deploy.

References:
[Azure DNS delegation](https://learn.microsoft.com/azure/dns/dns-domain-delegation),
[subdomain delegation](https://learn.microsoft.com/azure/dns/delegate-subdomain),
[Static Web Apps quotas](https://learn.microsoft.com/azure/static-web-apps/quotas).
