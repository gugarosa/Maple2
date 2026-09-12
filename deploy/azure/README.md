# Maple2 private Azure pilot

Use the same broad hosting pattern as MapleTime: **Porkbun DNS, an Azure Static
Web App for the website, and a separate VM/static IPv4 for the native game**.
MS2 resources remain isolated in `rg-maple2-brazilsouth`.

The earlier Azure DNS child-zone plan was withdrawn. Its unused, undelegated zone
was removed. **Do not add the four NS records from that earlier plan.** No parent
Porkbun nameservers, MS1 apex/www/game records, or mail records were changed.

## Pilot boundaries

The pilot is private, not a public game launch. Deallocate a bootstrap-only VM
after validation. The operator authorized a running application instance on
2026-09-12; that private pilot remains subject to the existing budget guard.

| Resource | Configuration |
|---|---|
| Resource group | `rg-maple2-brazilsouth` |
| Network | `vnet-maple2-brazilsouth`, `10.43.0.0/16` |
| Subnet / NSG | `snet-maple2`, `10.43.1.0/24`; `nsg-maple2`, restricted application ingress |
| VM | `vm-maple2-brs`, `Standard_B2als_v2`, 2 vCPU / 4 GiB |
| OS / data disks | Separate 32-GiB Standard SSD disks; preserve existing disk IDs/sizes |
| Static IPv4 | `pip-maple2-brs`, initially `20.226.79.46` |
| Website | Free `swa-maple2-lx7rwls5nb4z2` in East US 2 |
| Generated site | `blue-flower-07ce5190f.3.azurestaticapps.net` |
| Storage | `stmaple2lx7rwls5nb4z2`, private `backups` and `artifacts` containers |
| Key Vault | `kv-maple2-lx7rwls5nb4z2`, separate managed-identity access |
| Budget | `budget-maple2-monthly`, BRL 250/month initial guard |

Read current addresses from deployment outputs before publishing DNS. The VM's
public IP provides explicit outbound connectivity. The foundation alone opens
no ingress; application rules are separate. No original client archives or local
player database are uploaded.

The infrastructure bootstrap prepares Docker/Compose and a guarded data mount only. It does
**not** install the game services, import metadata, create game accounts, schedule
application backups, or enable HTTPS registration. The private backup container
and vault are infrastructure capabilities, not evidence that those application
workflows have been deployed.

## Running application pilot

The application uses the same VM and disks, with fresh Azure accounts separate
from local development. Only prepared static metadata and navigation were copied;
local characters and uploaded player images were not.

| Surface | Address |
|---|---|
| HTTPS account registration | `https://play.ms2.mapletime.dev/account` |
| Native Login | `20.226.79.46:20001` |
| Instanced / normal Game | `20.226.79.46:20002` / `20.226.79.46:20003` |
| Native Web assets | `http://20.226.79.46:4000`; registration is blocked here |
| Exact server source | `https://play.ms2.mapletime.dev/source/server-source.tar.gz` |
| Independent website | `https://ms2.mapletime.dev` |

HTTPS, game ports and native Web access are restricted to the approved
workstation's public IPv4 `/32`. Other networks are intentionally not admitted.
Port 80 accepts ACME HTTP-01 validation; ordinary requests receive 404.
MySQL has no host-published port. World gRPC is host-loopback only. Neither SSH
nor gRPC is opened in the NSG.

`application-access.bicep` adds private artifact storage, VM read access and
container-scoped operator upload access. `application-ingress.bicep` manages only
the two named access rules. Review what-if and validate one IPv4 `/32` before
applying it; never substitute `Internet` or `0.0.0.0/0` for the client prefix.

The group has `application=private-pilot`. The foundation wrapper refuses
`-Apply` for an application-owned group because its empty NSG would erase these
rules. The infrastructure-only pilot wrapper also refuses custom ingress.
Do not use either bootstrap procedure for application restarts.

### Version and runtime

The Release images were built from a complete, checksummed server-source snapshot,
without committing or pushing the working changes:

- Git base: `ddfb60bf6a737cdc1f7d7e141dd76941f8eea8b6`.
- Source SHA-256: `c48bfebb77eb1a5183a5b6785b31e9bf8fc5f34ce794e6d2263a123929c7d33d`.
- Active configuration: `20260912-c48bfebb77eb-a07b9801`.
- Manifest SHA-256: `571021939b1c8e28be78cf563a4aa2f9541b0a29ff97dba301763630bc0c1ef3`.

Private artifacts include six pinned Linux/amd64 images, the source, an EF
migration bundle, static metadata, navigation and configuration. Checks accept
the verified OCI platform-manifest or image-config digest because Docker's
classic and containerd stores report different image identity forms.

`compose.application.yml` is separate from local Compose. Dockerfiles retain the
local Debug default and accept `BUILD_CONFIGURATION=Release` for deployment.
The application DB user has metadata `SELECT` and game-database CRUD only.
Separate root/runtime passwords live in the MS2 vault; only initialization and
migrations use root. The VM's `.env` is root-only, not part of any image/source
archive, and root credentials are not injected into application containers.

Caddy shares Web's network namespace and forwards over loopback to port 4001.
`WEB_PORT=4000` is still the native client's advertised HTTP port; `WEB_BIND_PORT`
controls only the backend listener. ASP.NET retains its loopback forwarding trust
boundary. `REQUIRE_HTTPS_REGISTRATION=true` rejects insecure account requests even
with forged headers. Web data-protection and TLS keys have retained volumes.

The 4-GiB pilot is small. Bulk JSON import temporarily grants MySQL 2 GiB while
other applications are stopped, then recreates it at its runtime limit.
Cold-request checks required these runtime ceilings: MySQL 1 GiB, World 512 MiB,
Login 384 MiB, Web 512 MiB, each Game 512 MiB, Caddy 128 MiB (3.5 GiB total).
This is not a public-population capacity claim. An 8-GiB resize was not approved
or performed; the budget and VM size remain unchanged.

Later pre-deployment inspection found three Login memory-limit restarts and
1,430 threads/1,019 file descriptors. Quiet TCP peers, including health probes,
were retained after EOF because the receive-pipe writer was not completed.
The shared session path now completes that writer in `finally`; one-connection
and repeated-disconnect regressions reproduce the old failure and pass with the
fix. This addresses the leak without raising memory limits or resizing the VM.

### Restart, backup and restore

Use Azure Run Command, not public management ports. The active release is linked
at `/srv/maple2/current`.

```bash
sudo flock /run/maple2-operation.lock /srv/maple2/current/start-application.sh
sudo /srv/maple2/current/backup-application.sh
```

Backups use a short maintenance window. Game writers stop before Web/World;
the game database, uploads, data-protection keys, TLS state and release manifest
are archived and uploaded through managed identity to `backups/application/`.
Services restart afterward. The supplied systemd timer schedules 05:00 UTC.
Do not archive a changing upload directory and call it a consistent DB/files backup.

The downloaded archive/checksum and a restore into a disposable MySQL schema were
verified on 2026-09-12. Replacing live player data remains an explicit maintenance
decision, never an automatic fallback. Retain the static metadata/navigation
artifacts named by the release manifest.

`update-configuration.sh` permits source/image/static-data-identical updates and
takes a backup first. Failed legacy configuration starts restore the previous
release and rerun its image/container-health checks; failed recovery is explicit.
The updater also rejects a changed active release before stopping writers.
`install-application.sh` is initial-installation-only and
refuses to overwrite an initialized application. Code/schema upgrades need a
separately reviewed migration and rollback procedure.

### Merge-triggered CI/CD

The authored `Deliver Maple2` workflow runs on `master` updates, including merged
PRs, or a manual dispatch from `master`. Reusable test and format workflows run
first; PRs keep their existing checks. A Linux `deployment-contracts` job exercises
package validation, backup/failure/rollback control flow and a real Docker image
archive round-trip using inert images.

Delivery is **not live yet**. Azure/GitHub identity configuration has been
provisioned, but `MS2_CD_ENABLED=false`. The runtime/deployment changes are prepared
for review, not activated on the running server. The application was originally
built from a verified snapshot ahead of published `master`. Merge the reviewed
runtime/deployment baseline, pass its
hosted checks, then enable delivery; do not deploy the older Git revision over
the running pilot merely to exercise CI.

The pipeline:

1. Requires passing tests/formatting and the current, committed `master` SHA.
   Forks, PR jobs, other branches and superseded revisions cannot promote code.
2. Builds Linux/amd64 Release images for Game, World, Login and Web from an exact
   source archive. Tags include the commit and cryptographic image identity so
   rebuilding a commit cannot replace the previous rollback image.
3. Uses short-lived GitHub OIDC authentication to upload checksummed artifacts to
   the existing private MS2 container. There is no stored Azure client secret,
   registry subscription, SSH credential or public management port.
4. Uses Azure Run Command to acquire the existing maintenance lock, validate
   source/image fingerprints and unchanged data contracts, then take a consistent
   player/uploads/keys/TLS backup while writers remain stopped.
5. Starts the candidate and checks seven healthy services, complete v12
   handshakes, actual World channel 1, HTTPS registration, native HTTP isolation
   and the exact served source. Only then is the current-release link advanced.
6. On backup, startup or health failure, restores and verifies the previous
   application version. Failed rollback is an explicit failure requiring operator
   recovery. It never silently reports success or automatically restores a live
   database.

Deployments include a maintenance interruption: players are checkpointed and
disconnected before the services stop, then can reconnect after recovery. This
does not install a new native client or publish the quarantined Windows bootstrap.
The probes run on the VM because GitHub-hosted runners are not admitted by the
pilot's `/32` ingress. They are not a substitute for external gameplay acceptance.

**Automatic promotion is limited to data-compatible code releases.** Migration,
database model/context/converter, model-project, metadata-storage, importer and
selected database-tool changes stop before downtime for operator review.
The entire `Maple2.Model` project is gated because EF-owned and JSON-persisted
types also live in its Game, Common and Enum directories. This deliberately
includes model-only edits that may turn out not to require a migration.
The updater preserves MySQL/Caddy images, database identities, persistent mounts,
published ports and the existing memory ceiling. It neither re-ingests metadata
nor executes EF migrations. Code fixes that do not change those contracts can be
delivered automatically; schema/data/vendor/infrastructure upgrades remain separate
maintenance work.

The `release.py` helper reads both classic Docker and OCI save formats and verifies
their cryptographic identities. `deploy-release.sh` handles promotion/recovery.
The generated Run Command wrapper explicitly selects Bash, including when the VM
agent initially starts it with `/bin/sh`; its shell handoff has a regression check.
The backup helper's internal `--deployment` mode requires the inherited
maintenance lock and deliberately leaves writers stopped for its caller.
CI-managed releases reject the older configuration-only updater.

Provision or inspect access from this workstation:

```powershell
.\deploy\azure\configure-cicd.ps1 -SubscriptionId $subscription
.\deploy\azure\configure-cicd.ps1 -SubscriptionId $subscription -Apply

# Only from the clean, published master baseline after all hosted checks pass:
.\deploy\azure\configure-cicd.ps1 -SubscriptionId $subscription -Apply -Enable
```

The separate `id-maple2-github-deploy` identity trusts only
`repo:gugarosa/Maple2:environment:maple2-pilot`. That GitHub environment admits only
the `master` branch. Its Azure assignments are resource-group Reader, Blob Data
Contributor on the MS2 artifact container, and a custom Run Command-only role
scoped to `vm-maple2-brs`. No MS1 resources or subscription-wide deployment roles
are used. Enabling delivery also requires protected PR merges with `build`,
`format` and `deployment-contracts` checks, and rejects conflicting existing
protections instead of weakening them.

GitHub intermediate artifacts expire after one day. VM/private-blob releases are
retained for recovery; monitor their growth and preserve the active and rollback
releases plus the original static-data artifacts during any reviewed cleanup.
Low disk space blocks promotion rather than pruning recovery data or player volumes.
Compose versions can encode byte limits as JSON numbers or numeric strings; both
are validated against the same ceiling, and missing or unbounded limits are rejected.
A successful hosted run is required before reporting automatic redeployment as
operational.

### Access evidence

External checks confirmed all three complete v12 game handshakes, trusted HTTPS,
secure antiforgery cookies, actual registration/rejection/throttling behavior,
native assets and the exact source hash. The original client authenticated the
new Azure account and received the server-list response. Character selection,
world entry and sustained gameplay still need interactive confirmation; the
earlier local rendering/automation limitation is not a cloud gameplay sign-off.

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
contract. The website publication did not deploy server code, enable automatic
server delivery, change application ingress or publish a native installer.

`scripts\test_pilot_portal.ps1` checks these boundaries and compares the endpoint,
client version and launcher release to `installer\distribution.json`.

Web supports optional `PLAYER_WEBSITE_URL` for a setup link on its account and
success pages. The authored Azure Compose value is
`https://ms2.mapletime.dev/#getting-started`; startup rejects non-HTTPS or
credential-bearing URLs. This Web code/configuration change is not in the active
Azure release and must use a reviewed code-release upgrade, not the
source-identical configuration updater.

The redesign was published on 2026-09-12 through website-only
[PR #9](https://github.com/gugarosa/Maple2/pull/9), revision
`f73db9073e932cb1042a7abd568dba0de49afd1c`, to the existing Static Web App.
Exact live content and 20 browser cases passed on `https://ms2.mapletime.dev`.
No server, installer, server-CD or ingress changes were deployed with it.
It uses the authorized MS2 artwork rather than the draft's generic island.
The MapleTime cube mark remains original identity artwork, separate from the
official game logo.

Both color schemes follow the browser's preference. The mobile menu uses native
HTML disclosure; there are no script, font, or external image dependencies.
Keep the setup/status anchors stable, link updates to real merged PRs, and do not
advertise MS1 commands, shared accounts, a bundled game installer, or untested
MS2 feature completeness.

The public website is reachable independently of application ingress. Its
availability does not make the `/32`-restricted registration and game ports
available to other players. Follow the
[website-to-game launch gates](../../CLIENT_SETUP.md#website-to-game-launch)
before changing that boundary, publishing downloads or promising public play.

When explicitly authorized to publish, use the clean, validated `origin/master`
revision:

```powershell
.\deploy\azure\deploy-portal.ps1 -SubscriptionId $subscription `
    -StaticWebAppName swa-maple2-lx7rwls5nb4z2
```

The script checks the target's `project=maple2` tag and validates the static site
before retrieving deployment credentials. It uploads only the ten allowlisted
HTML, CSS, configuration, and artwork files using Microsoft's deployment client,
keeps the token outside the checkout, and compares every served page/style/image
with its exact uploaded bytes. A still-cached old page or missing image is not
deployment success. It cannot deploy to the MS1 site. Also verify
`https://ms2.mapletime.dev` after publication.

### Website artwork

On 2026-09-12, the project owner explicitly confirmed permission to use MS2
artwork for this MapleTime MS2 website. This is the basis for the selected images;
it is not a claim that the server's AGPL license, a public download, or Nexon's
general creator guide grants unrestricted artwork redistribution. MapleStory 2
art remains copyright NEXON. The website retains attribution and its independent,
non-commercial, non-affiliated status. This permission does not change the
original-client/archive distribution boundary in [CLIENT_SETUP.md](../../CLIENT_SETUP.md).

Only two official source images are used, both from Steam's MapleStory 2
application `560380`:

| Source | Dimensions | SHA-256 |
|---|---|---|
| [Library hero](https://cdn.cloudflare.steamstatic.com/steam/apps/560380/library_hero.jpg) | 1920 x 620 | `143f7570c9dce1397ea6b72ff186e42aacd6c0baff697dc8bf627169b7975675` |
| [Game logo](https://cdn.cloudflare.steamstatic.com/steam/apps/560380/logo.png) | 640 x 360 | `88cc543dddb63b211edccb31530a75a8b17c2ce9912843ca5578b30ae236bc96` |

The checked-in website derivatives were produced with Pillow 12.1.1. Crop boxes
below use `(left, top, right, bottom)` in original source pixels, with right/bottom
exclusive. No image is enlarged, repainted, or extracted from the local client.

| Website file | Source and conversion |
|---|---|
| `ms2-world.webp` | Full hero, WebP quality 88, method 6 |
| `ms2-world-mobile.webp` | Hero crop `(748, 0, 1876, 620)`, WebP quality 88, method 6 |
| `ms2-logo.png` | Logo crop `(0, 57, 640, 303)` removes only transparent padding; optimized PNG |
| `ms2-slime.webp` | Hero crop `(176, 43, 292, 159)`, WebP quality 90, method 6 |
| `ms2-pig.webp` | Hero crop `(1771, 0, 1873, 102)`, WebP quality 90, method 6 |
| `ms2-mushroom.webp` | Hero crop `(1562, 129, 1632, 199)`, WebP quality 90, method 6 |

The mobile hero keeps the main class characters in frame instead of squeezing
the desktop banner into a narrow column. News cards use different MS2 monsters,
not MS1 sprites. `scripts\test_pilot_portal.ps1` pins all six derivative hashes and
rejects unused or unreviewed image references. The site serves them locally under
`img-src 'self'`; no third-party image request or new runtime package is needed.

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
