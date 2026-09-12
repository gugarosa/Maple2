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

`website` is a script-free MS2 site with the same broad layout as MS1: navigation,
a hero, setup cards, project updates and status. It uses approved MS2 artwork,
distinct monsters, light/dark themes, a native mobile menu and concise setup help.
The website can be published independently of the installer and public-game gates.

The separate Azure application is running as an invite-only pilot. The website
links its HTTPS registration page for approved networks, one official Mushroom
release and manual connection instructions. No game-client or MapleTime installer
download is advertised while their release gates remain blocked. The site neither
collects credentials nor claims to be a live server-health monitor.

`installer\distribution.json` is shared version/endpoint/artwork metadata, not an
installer binary. The portal checks compare the player-facing details with that
contract. This website release does not deploy server code, enable automatic
server delivery, change application ingress or publish a native installer.

From the clean, validated `origin/master` revision:

```powershell
.\deploy\azure\deploy-portal.ps1 -SubscriptionId $subscription `
    -StaticWebAppName swa-maple2-lx7rwls5nb4z2
```

The script checks the target's `project=maple2` tag and uploads only the explicit
HTML/CSS/configuration/artwork allowlist using Microsoft's deployment client.
It keeps the token outside the checkout and verifies exact live HTML, CSS and
image bytes rather than accepting HTTP 200 with stale content. It cannot deploy
to the existing MS1 site. Also verify `https://ms2.mapletime.dev` after publication.

### Website artwork

On 2026-09-12, the project owner explicitly confirmed permission to use MS2 artwork
for this website. This project-specific permission does not license the original
game archives or imply unrestricted artwork redistribution. The site retains
NEXON attribution and its independent, non-commercial, non-affiliated status.

Only two official images from Steam application `560380` are used:

| Source | Dimensions | SHA-256 |
|---|---|---|
| [Library hero](https://cdn.cloudflare.steamstatic.com/steam/apps/560380/library_hero.jpg) | 1920 x 620 | `143f7570c9dce1397ea6b72ff186e42aacd6c0baff697dc8bf627169b7975675` |
| [Game logo](https://cdn.cloudflare.steamstatic.com/steam/apps/560380/logo.png) | 640 x 360 | `88cc543dddb63b211edccb31530a75a8b17c2ce9912843ca5578b30ae236bc96` |

Derivatives use Pillow 12.1.1. Crop coordinates are `(left, top, right, bottom)`,
with right/bottom exclusive. No client files were used, and no image was enlarged.

| Website file | Source and conversion |
|---|---|
| `ms2-world.webp` | Full hero; WebP quality 88, method 6 |
| `ms2-world-mobile.webp` | Hero crop `(748, 0, 1876, 620)`; WebP quality 88, method 6 |
| `ms2-logo.png` | Logo crop `(0, 57, 640, 303)` removes transparent padding; optimized PNG |
| `ms2-slime.webp` | Hero crop `(176, 43, 292, 159)`; WebP quality 90, method 6 |
| `ms2-pig.webp` | Hero crop `(1771, 0, 1873, 102)`; WebP quality 90, method 6 |
| `ms2-mushroom.webp` | Hero crop `(1562, 129, 1632, 199)`; WebP quality 90, method 6 |

The mobile crop keeps the class characters in frame. Updates use distinct MS2
monsters, not MS1 sprites. `scripts\test_pilot_portal.ps1` pins the derivative
hashes and rejects unreviewed/external assets. Images are served locally under
`img-src 'self'`; there are no JavaScript, font or third-party image dependencies.

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
