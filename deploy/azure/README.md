# Maple2 private Azure pilot

Use the same broad hosting pattern as MapleTime: **Porkbun DNS, an Azure Static
Web App for the website, and a separate VM/static IPv4 for the native game**.
MS2 resources remain isolated in `rg-maple2-brazilsouth`.

The earlier Azure DNS child-zone plan was withdrawn. Its unused, undelegated zone
was removed. **Do not add the four NS records from that earlier plan.** No parent
Porkbun nameservers, MS1 apex/www/game records, or mail records were changed.

## Pilot boundaries

The pilot is private, not a public game launch. The VM is to be **deallocated
after operator bootstrap validation**, rather than left running continuously
against the shared Visual Studio subscription credit.

| Resource | Configuration |
|---|---|
| Resource group | `rg-maple2-brazilsouth` |
| Network | `vnet-maple2-brazilsouth`, `10.43.0.0/16` |
| Subnet / NSG | `snet-maple2`, `10.43.1.0/24`; `nsg-maple2`, no custom ingress |
| VM | `vm-maple2-brs`, `Standard_B2als_v2`, 2 vCPU / 4 GiB |
| OS / data disks | Separate 32-GiB Standard SSD disks; preserve existing disk IDs/sizes |
| Static IPv4 | `pip-maple2-brs`, initially `20.226.79.46` |
| Website | Free `swa-maple2-lx7rwls5nb4z2` in East US 2 |
| Generated site | `blue-flower-07ce5190f.3.azurestaticapps.net` |
| Backup storage | `stmaple2lx7rwls5nb4z2`, private `backups` container |
| Key Vault | `kv-maple2-lx7rwls5nb4z2`, separate managed-identity access |
| Budget | `budget-maple2-monthly`, BRL 250/month initial guard |

Read current addresses from deployment outputs before publishing DNS. The VM's
public IP provides explicit outbound connectivity, but the foundation NSG opens
no game, SSH, database, or web ingress. No original client archives or local
player database are uploaded by these templates.

The bootstrap prepares Docker/Compose and a guarded data mount only. It does
**not** install the game services, import metadata, create game accounts, schedule
application backups, or enable HTTPS registration. The private backup container
and vault are infrastructure capabilities, not evidence that those application
workflows have been deployed.

## Costs and the shared credit

Standard retail reference prices observed on 2026-09-11:

- MS2 4-GiB VM: USD 0.0605/hour, about USD 44.17 for 730 running hours.
- The 8-GiB alternative: about USD 88.33/month for compute alone.
- Retaining all three MS1 website names requires Standard: approximately USD 9/month.

Disks, retained IPv4, backup transactions, and traffic are additional. These are
reference prices, not a BRL bill or a guarantee about subscription credit.
Deallocating stops VM compute billing, not the smaller retained-resource charges.
Do not remove the subscription spending limit to make an oversized pilot fit.

The MS2 budget emails at 80% actual spend and 100% forecast, and requests VM
deallocation at 100% actual spend. Its managed identity can only read/deallocate
VMs in the MS2 group. The workflow targets `vm-maple2-brs`, never the Cosmic VM.
Budget alerts are delayed and are **not an instantaneous spending cap**.

## Preview and provision

Use explicit subscription selection. No script changes Azure CLI's global
default subscription. The foundation wrapper rejects a group with a different
project tag or region.

```powershell
az account list --query "[].{Name:name,Id:id}" --output table
$subscription = "<the intended subscription ID>"

# Group/network only; DNS stays at Porkbun.
.\deploy\azure\deploy-foundation.ps1 -SubscriptionId $subscription
.\deploy\azure\deploy-foundation.ps1 -SubscriptionId $subscription -Apply

# Preview the private pilot before provisioning paid resources.
.\deploy\azure\deploy-pilot.ps1 -SubscriptionId $subscription `
    -AdminSshPublicKeyPath "$env:USERPROFILE\.ssh\maple2-azure-ed25519.pub" `
    -AlertEmail "operator@example.org"
```

Add `-Apply` only after reviewing what-if. The pilot wrapper verifies BRL billing,
the private foundation, the operator identity, and existing disks/budget dates.
If Cost Management is throttled, pass `-BillingCurrencyBudgetId` with an existing
budget resource ID **in the same subscription** whose `currentSpend.unit` confirms
BRL. This is a read-only currency reference, not permission to modify that budget.
The current MS2 budget can serve as that reference on subsequent deployments.

Existing VM updates preserve the actual OS disk name/size, LUN0 disk name, and
administrator username. Existing data disks are not redeclared or resized.
A replacement VM requires explicit `-ExistingDataDiskName`; an existing blank
disk is never formatted automatically.

The new-disk bootstrap verifies the Azure LUN0 device, absence of partitions and
signatures, and a full zero-filled-device read before formatting. It mounts by
UUID at `/srv/maple2`, puts Docker data there, bounds Docker logs, and requires
that mount before Docker starts. There is no silent OS-disk fallback.

## Validate bootstrap, then stop the pilot VM

Use Azure control-plane Run Command; do not open SSH:

```powershell
az vm run-command invoke --subscription $subscription `
    --resource-group rg-maple2-brazilsouth --name vm-maple2-brs `
    --command-id RunShellScript `
    --scripts "cloud-init status --wait --long; findmnt /srv/maple2; docker info; docker compose version"

az vm deallocate --subscription $subscription `
    --resource-group rg-maple2-brazilsouth --name vm-maple2-brs
```

Require successful bootstrap, the correct mounted UUID, and Docker root
`/srv/maple2/docker`; merely reaching VM `Succeeded` is not a bootstrap check.
Review the stopped VM state afterward. Before any future application rollout,
add metadata/migration handling, protected secrets, tested backup/restore,
resource limits, and private multi-client validation.

## Website deployment

`website` contains only authored static HTML/CSS and Static Web Apps configuration.
The page explicitly states that registration/public gameplay are not open.
It does not collect credentials or pretend to be a live health monitor.

From the clean, validated `origin/master` revision:

```powershell
.\deploy\azure\deploy-portal.ps1 -SubscriptionId $subscription `
    -StaticWebAppName swa-maple2-lx7rwls5nb4z2
```

The script checks the target's `project=maple2` tag, uploads only three approved
static files using Microsoft's deployment client, keeps the token in a temporary
file outside the checkout, and verifies the generated HTTPS endpoint. It cannot
deploy to the existing MS1 site.

## Porkbun records

Keep all current MS1 apex/www/play, DKIM, SPF, and verification records.
Add these only for the actual provisioned targets:

| Type | Host field | Answer | TTL |
|---|---|---|---|
| `CNAME` | `ms` | `red-cliff-08e48be0f.7.azurestaticapps.net` | 600 |
| `CNAME` | `ms2` | `blue-flower-07ce5190f.3.azurestaticapps.net` | 600 |
| `A` | `play.ms2` | `20.226.79.46` | 600 |

The records require Porkbun access; creating Azure resources does not publish
them. No MS2 NS delegation is required. If those old NS records were added,
remove only the four `ms2` delegation records before adding the CNAME.

The native game name is separate from the Static Web App frontend: a static
website cannot proxy arbitrary game TCP traffic. `play.ms2` resolving to the VM
does not mean gameplay is open while the VM is deallocated or ingress is blocked.

After the CNAMEs are publicly visible, validate the managed website bindings:

```powershell
az staticwebapp hostname set --subscription $subscription `
    --resource-group rg-cosmic-brazilsouth --name swa-mapletime-w473ymmg3j5z2 `
    --hostname ms.mapletime.dev --validation-method cname-delegation

az staticwebapp hostname set --subscription $subscription `
    --resource-group rg-maple2-brazilsouth --name swa-maple2-lx7rwls5nb4z2 `
    --hostname ms2.mapletime.dev --validation-method cname-delegation
```

`.dev` is HSTS-preloaded. Both bindings and their certificates must become `Ready`
before calling these usable website addresses. The MS1 canonical origin stays
`mapletime.dev`; its current OAuth callbacks/cookies and existing bindings are not
changed. Standard capacity was requested to add the alias without removing `www`.

No credentials for Porkbun are stored in this repository. Do not publish a
registration URL or open game ingress while the DNS/HTTPS/application gates remain
unverified.

## Validation

```powershell
az bicep build --file .\deploy\azure\foundation.bicep --outfile "$env:TEMP\maple2-foundation.json"
.\scripts\test_azure_foundation.ps1 -CompiledTemplate "$env:TEMP\maple2-foundation.json"
az bicep build --file .\deploy\azure\pilot.bicep --outfile "$env:TEMP\maple2-pilot.json"
.\scripts\test_azure_pilot.ps1 -CompiledTemplate "$env:TEMP\maple2-pilot.json"
.\scripts\test_pilot_portal.ps1
```

These checks do not log into or deploy Azure resources. See
[CLIENT_SETUP.md](../../CLIENT_SETUP.md) for the separate original-client,
distribution, and remote-game protocol constraints.
