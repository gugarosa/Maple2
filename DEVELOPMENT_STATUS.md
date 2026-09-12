# Development Status

This document describes the current implementation. It does not imply that every upstream issue is fixed.

[UPSTREAM_ISSUES.csv](UPSTREAM_ISSUES.csv) contains all 85 issues open upstream at the
2026-09-06 review snapshot, with per-issue implementation and evidence status.
An upstream issue remaining open does not establish that its original defect is
still present in this fork.
The 2026-09-11 recheck found no new upstream commits or open-issue updates since
that snapshot.

## Validated integration baseline (2026-09-11)

The following checks completed against isolated real client metadata and an isolated database:

- All 11 solution projects, including `Maple2.Server.DebugGame`, compile.
- The regular Release NUnit suite passes: 505 tests. Another 121 explicitly
  selected MySQL persistence tests pass in throwaway game databases using the actual
  migrations, including the disjoint account/character/mail identifier ranges.
  These cover accounts/quests (22), guild rewards (2), transaction failures (6),
  item/mail/trade/blueprint ownership (50), market/shop behavior (24), and resets (17).
- Read-only client archive checks cover all normalized trigger definitions and the
  actual Horus carrier patrol references. The quest archive regression checks all
  4,903 timing/faction mappings and the item/portal/furnishing/guild reward examples
  without changing client assets.
- Full read-only ingestion with parser 2.4.24 completed 28,620 NIFs, 2,518 physics
  meshes, 1,183 processed maps, 1,869 map-data records, and 235,082 map entities.
- SQL verification confirmed merged client/server constants, client pet slots, premium potion `90000409` `useItem`, 20 Toxic Garden weapons, 11 Henesys bombs, and Frey recovery metadata.
- The Docker World, Login, Web, `game-ch0`, and `game-ch1` stack starts successfully. Both Game health endpoints report `Healthy`, and Login plus both Game client ports return 25-byte handshakes.
- Canonical Game-only recreation preserves the running World container and its
  normal channel ID 1. The actual v12 client authenticates a registered ordinary
  account, creates a character, enters the world, moves, and exits normally.
- HTTP registration checks verify invalid/mismatched input, antiforgery rejection,
  successful atomic registration, duplicate rejection, and rate limiting.
- Web uploads and all 1,181 existing navigation files survive forced container
  recreation. Game navigation mounts are read-only. Original client executable
  hashes remain unchanged; no administrator elevation or binary patch was needed.
- The refreshed service images shut down with exit code 0. MySQL has a one-minute
  stop grace period so volume flushing is not cut short by Docker's default timeout.
- The account-wide dungeon migration completed an upgrade, rollback, and re-upgrade
  against a separate disposable MySQL schema.
- The guild-reward ledger migration also completed an upgrade, rollback, and
  re-upgrade in an empty, isolated schema. Quest persistence regressions cover
  concurrent acceptance, session-save lock ordering, failed activation, pending
  inventory saves, and retained completion history.

This is an integration baseline, not evidence that every gameplay issue or reverse-engineered protocol flow is complete.

## End-to-end recheck (2026-09-12)

The local service and persistence checks passed, but this run does **not** provide
a new native-client gameplay sign-off:

- Docker Desktop was initially stopped. After starting it, the existing six-service
  `maple2-offline` stack recovered using its retained MySQL, Web-data and navigation
  volumes. No metadata re-ingestion or player-database replacement was needed.
- All 11 solution projects built in Release. The regular suite passed 506 tests;
  another 121 real-MySQL persistence tests and five read-only client archive tests
  passed using a complete, separate metadata copy and disposable player schemas.
  Temporary schemas were removed afterward.
- The isolated run found that the historical `InteractCubeFix` migration qualified
  its cleanup with `GAME_DB_NAME`, potentially targeting a different database.
  Its SQL now uses the migration connection's database. A regular regression and
  actual migrations with a deliberately nonexistent environment database cover
  the corrected scope. Previously applied migrations are not replayed.
- Live HTTP checks covered registration, invalid input, antiforgery, duplicate
  rejection and throttling. The running World account service accepted the
  existing ordinary account's password, rejected an incorrect password and an
  invalid client identity, and left that account's identity, permissions and
  version unchanged.
- Full shutdown returned exit code 0 for all six services. Full startup and
  game-only recreation restored normal channel 1 and complete v12 handshakes on
  all three client ports. Account, character, item and home counts were preserved;
  game-only recreation retained the World container. The 1,181 navigation meshes
  and their checksum sidecars remain on read-only Game mounts. Existing banner
  and profile images were served byte-for-byte from the retained Web volume;
  missing images returned HTTP 404.
- Channel advertisement follows World's asynchronous health monitoring, not the
  instant a container first becomes healthy. Runtime verification waits for the
  actual channel list with a deadline rather than treating one early empty list
  as a persistent failure.
- The local website's assets, navigation, security headers and responsive layout
  passed their checks. At this stage, the published HTTPS site was a private-pilot
  information page and the Azure VM was deallocated. The later application
  deployment is recorded below.

Official Mushroom 2.0.3 launched the canonical original client with the correct
local endpoint. However, automated window capture returned no rendered frames
and focus control was unreliable. Manual login, character/world entry, movement,
and reconnect were **not observed in this recheck**. A smaller viewport did not
resolve the verification limit; the original display preferences were restored
byte-for-byte and the owned client/launcher were closed. The earlier client
results above remain historical evidence, not a substitute for this missing check.

## Private Azure application (2026-09-12)

The operator-authorized pilot now runs on `vm-maple2-brs`, using the original
4-GiB VM, retained disks and BRL 250 budget guard. No local accounts, characters or
uploaded player images were copied. The deployment uses a checksummed Release
source/image snapshot; it does not imply that the working changes were committed.
See [Azure operations](deploy/azure/README.md#running-application-pilot).

- All seven services are healthy. World advertises normal channel 1, and the
  native ports return complete v12 handshakes from this workstation.
- `https://play.ms2.mapletime.dev/account` passed real registration, antiforgery,
  invalid-input, duplicate and throttling checks. Secure cookies and trusted
  loopback forwarding were verified. Native HTTP serves game assets but rejects
  account routes, including forged forwarding headers.
- The original client authenticated the new Azure account and received the
  server-list response. Its device identity was bound by the real login flow.
  Character selection and world entry still require interactive confirmation.
- The exact server-source archive is available through the private HTTPS endpoint.
  The application DB identity has metadata read and game CRUD permissions only.
  MySQL, SSH and gRPC are not reachable externally.
- A downloaded application backup was checksum-verified and restored into a
  disposable MySQL schema, without replacing live data. The 05:00 UTC backup timer
  is active. Backup/restart and subsequent external access checks passed.

Cold initialization exposed limits that steady local memory readings did not
predict. Bulk metadata import now gets temporary MySQL headroom with other apps
stopped; normal service limits were rebalanced and verified after real requests.
This restricted pilot is not a public-load or full-gameplay capacity sign-off.
Only the approved workstation network can reach the application ports.

## Windows installer status (2026-09-12)

The per-user MS2 bootstrap is implemented, but **not cleared for distribution**.
It reuses official Mushroom, accepts an existing compatible client, pins publisher
downloads, and carries its exact authored source without redistributing game files.
Source/build contracts pass on Windows PowerShell 5.1 and 7. The real preview
installed the expected helpers and shortcuts while preserving profile values,
credentials and client files; its separately scanned uninstaller removed those
helpers and shortcuts without removing Mushroom or player settings.

Windows Defender subsequently quarantined the real setup EXE and synthetic
fixtures as `Trojan:Win32/Bearfoos.A!ml`, despite initial no-threat scans. No
false-positive determination has been made. The executable is withheld, no
security protection was weakened, and complete repeat-install/clean-PC acceptance
remains blocked. See [installer verification](CLIENT_SETUP.md#installer-verification-status)
for the evidence, manual alternative and remaining release gates.

## MS2 website redesign (2026-09-12)

The website published through [PR #9](https://github.com/gugarosa/Maple2/pull/9)
now connects invite-only registration, compatible-client
requirements and manual Mushroom connection steps without advertising a missing
installer or game download. Web's optional validated `PLAYER_WEBSITE_URL` return
link and matching registration-page design are now deployed. The static website revision is
`f73db9073e932cb1042a7abd568dba0de49afd1c`, live at `https://ms2.mapletime.dev`.
Public player access still requires the
[website-to-game launch gates](CLIENT_SETUP.md#website-to-game-launch), including
approved client acquisition and fresh-PC world-entry evidence.
The live website passed exact-content checks and 20 responsive/light/dark/keyboard
cases. Separate local checks passed for the compiled Web build, URL guards and
loopback-only account/error pages with and without the return link. Missing
antiforgery and invalid registration still return HTTP 400; these checks did not
create accounts or clear the public-launch gates.
The registration page also matches the site's light/dark palette, with ten live
desktop/mobile/text-zoom cases checking layout, keyboard focus and input contrast.
It remains on the approved-network HTTPS account service, not an open public signup.

## Delivery automation status

Merge-triggered server delivery is operating, separately from the paused
gameplay-development work. It reuses PR tests/format checks, builds source-bound
Release images, takes quiesced backups and promotes only after runtime/source
checks, with application rollback on failure. Schema, metadata, vendor and
infrastructure changes require separate review; no player database is restored
automatically.

The MS2-only OIDC identity and master-only GitHub environment are active, with
protected PR merges and `MS2_CD_ENABLED=true`. The first successful automatic
rollout was [run 34721668981](https://github.com/gugarosa/Maple2/actions/runs/34721668981)
for commit `60b9fc18ad4c7469805f4884d7eff88a5d50a5e5`. Source/image identity,
registration, World channel 1, native assets/handshakes, backup checksum and retained
player counts were verified. Sixty-four external idle-peer probes completed, and
Login's post-check resource count was 17 threads rather than the previous 1,430.
VM size, memory ceilings, budget and ingress were not increased.

These delivery checks do not clear the installer or complete external-player
gameplay acceptance. See [CI/CD operations](deploy/azure/README.md#merge-triggered-cicd).

## Implemented in this fork

### Runtime and operations

- The [Azure private pilot](deploy/azure/README.md) isolates Maple2 in
  `rg-maple2-brazilsouth` with a non-overlapping network, Free Static Web App, and
  separate VM/IP, retained data disk, vault, backup container and budget guard.
  DNS stays at Porkbun; the unused Azure child zone was retired. Initial bootstrap
  prepares Docker/storage only. The separate application deployment adds HTTPS
  registration and workstation-restricted native access. Public gameplay remains
  gated independently from DNS, certificates and service health.
- Workspace and client distribution follow [CLIENT_SETUP.md](CLIENT_SETUP.md):
  one original client, one server checkout, and source-only setup artifacts in
  sibling `release`. Mushroom 2.0.3 uses its normal installed location and existing
  client-root selection. Native add-ons already match its bundled files; the
  genuine original Nx backup is retained. No proprietary game installer or remote
  service is claimed as published.
- Docker Compose keeps the custom two-channel topology: `game-ch0` is instanced content and `game-ch1` is the normal channel.
- MySQL, World, Login, and Game readiness checks gate startup. World, Login, and Game mount `config.yaml` read-only.
- Metadata ingestion is an explicit Compose profile and uses the repository-local EF Core 7.0.20 tool manifest.
- GitHub test and format workflows target .NET 8, use least-privilege read permissions, support fork pull requests, and never auto-commit.
- Login and Game session registries are concurrent and reconnect-safe. Shutdown stops listeners, notifies active/connecting clients, drains session sends, and disposes fields after disconnect begins.
- Normal TCP disconnect no longer uses abortive `SO_LINGER(true, 0)`, which could
  reset the client before its final login/migration response was consumed. A
  network regression reproduces the reset before the fix; actual client login,
  character selection, world entry, and directional movement work after it.
- Receive EOF now completes the pipe writer so orderly, idle peer disconnects
  release their session workers and socket. Deployment preflight exposed the
  resulting Login thread/file-descriptor leak and three memory-limit restarts.
  The new one-connection and repeated-close regressions failed before the fix;
  the regular Release suite now passes 508 tests. VM size and memory caps were
  not increased to hide the leak.
- Environment variables override `appsettings.json` in all five logging entrypoints.
  Authentication payloads are excluded from verbose packet traces.
- Re-registering a Game endpoint creates a fresh gRPC transport/monitor while
  retaining its channel ID and ports. Retired monitors cannot invalidate a
  replacement, and only active channels are admitted or advertised.
- Session persistence is serialized with item/currency operations. Failed component
  saves roll back instead of committing partial state; migration requires a
  confirmed save. Account leases carry owner tokens, expired holders cannot
  release replacements, and uncertain commits quarantine stale session state.
  Quarantine fences immediate and already-queued packet dispatch, disconnects after
  item operations unlock, and removes field/pet state without saving stale plots.
  Committed one-shot progression callbacks drain before field departure, final
  saves, and migration handoffs, outside item/save/lease locks. Timers are not
  replayed during this drain; callback failures prevent an unsafe handoff.
- Daily, weekly, and monthly resets update online accounts through checked session
  saves and retain the resulting concurrency tokens. Bulk account updates only
  affect offline accounts; resets cannot refresh a stale token over an unrelated
  database change. Login locks the account snapshot against bulk resets, and
  connecting sessions retain pending reset periods until initialization finishes.
  World and channel reset RPCs report failed session resets.
- Published asset paths no longer climb to the filesystem root. Web uploads and
  generated navmeshes use retained named volumes; Game mounts navigation read-only.
- Registration is explicit through the Web `/account` form, with input validation,
  antiforgery protection, request throttling, and a database-enforced unique
  username. Account/home creation commits together. Game login requires a
  registered account and verifies its BCrypt password in Debug and Release;
  empty credentials and implicit registration are removed.
- Newly created accounts have ordinary permissions and no Debug currency grants.
  Existing accounts are not rewritten. Registration passwords fit the observed
  16-character native client field; existing longer launcher-supplied passwords
  remain verifiable within BCrypt's 72-byte boundary without truncation.
- Missing user-stat metadata now fails explicitly. The obsolete level/job fallback and its hard-coded base-stat table were removed.

### Combat and items

- `damage start [object-id]`, `damage show`, and `damage stop` measure observed player, pet, and damage-over-time hits without changing combat.
- Enchant stat increases accumulate across enchant levels. Pre-enchanted generated gear receives the cumulative deltas for its current enchant level.
- Bank, pet, trade, and mail transfers persist destination stacks and source
  retirement together. Capacity failures do not destroy source items; canceled
  trades can return goods by durable mail when bags are full. Ownership/version
  checks reject stale cleanup writes and uncertain commits are not retried as
  ordinary delivery failures.
- Blueprint creation stages the actual persisted inventory UID, and publication
  preserves its owner. Shop stack sales pay for the actual quantity and retain the
  same buyback total; GameMeret prices debit GameMeret rather than regular Meret.
- Black-market purchase and cancellation use the same database stock claim.
  Buyer debit, stock/attachment ownership and seller payout commit together before
  in-memory receipts and cache notifications are applied.
- Standalone mail, trade, and blueprint currency transactions first checkpoint the
  full session, including the inventory and progress backing pending earnings.
  Monetary writes compare both saved versions and balances and preserve unrelated
  currency fields. These paths call the real session save directly; persistence
  regressions cover sold-item retirement, failed participant checkpoints, and
  queued progression at final saves. Notifications run outside the outer
  item-operation lock.
- Random item-option selection uses `Server.m2d` category weights, excludes explicit zero-probability options, preserves locked attributes, and prevents duplicate attribute lines.
- Server value distributions are used where present. Coverage is partial: 6,306 item-option IDs have client-defined value ranges but no server value distribution, so those values continue to use the existing range-based selection with a runtime warning.

### Progression and content

- Rested EXP is credited to total EXP and consumed using the typed canonical constant rate. The accumulation time unit in the available `restExp.xml` data is not established, so official home/offline accrual is not claimed as complete.
- Quest trigger detection handles requested quest states, job filtering, negation, and persisted exploration progress.
- Quest acceptance commits the quest and its acceptance items together, including
  pending inventory saves, stack updates, and account-owned furnishings. Full
  inventories, missing item metadata, and failed storage leave acceptance unapplied.
  Trusted restarts use the same item and summoned-portal path and preserve completion counts.
- Guild quest EXP and funds use authoritative World metadata and current database
  membership. Durable per-activation receipts make guild credit retry-safe; a credited
  activation cannot be abandoned and reaccepted to obtain another grant.
- Composite trigger conditions retain their nested AND/OR predicates, outer actions,
  and state transitions. Unsupported nested predicates cannot weaken an AND group.
- Trigger ingestion applies declared action renames and normalizes the verified
  source selector/enum spellings before runtime. NPC damage thresholds and wedding
  state predicates read their canonical metadata attributes.
- Event-spawned NPCs retain their declared patrol paths, including the Horus
  carrier segments that do not have an explicit scripted movement action.
- NPC task scheduling removes terminal queue heads and preserves successor starts
  requested during resume callbacks, rather than stranding valid queued work.
- Combat-exit AI dispatch runs the declared `battleEnd` sequence and lets pending
  actions finish, without evaluating combat-only reserved branches out of combat.
  Ingestion keeps battle-end entries separate from battle entries; a real-AI
  archive regression covers this metadata-to-dispatch boundary.
- Dungeon mission scoring, weekly rank-reward persistence, atomic mail delivery, and stale-week cleanup are implemented.
- Weekly rank cleanup preserves claims made in the current week, including delayed
  or repeated reset callbacks; the scheduled boundary is Friday midnight.
- Dungeon records distinguish account-wide and character-wide ownership. Existing character records are transactionally merged when an account-wide record is initialized.
- Colosseum rounds use parsed gear-score requirements and round rewards. Hidden-result clears still run completion logic without showing the result UI.
- Random-room metadata drives room selection, duration, portal model, capacity, and auto-close. A probability roll runs on the first active field tick; a recurring spawn interval is not established by the available data.
- Room admission counts loading clients as well as present players. Session-owned
  reservations are released on failed/abandoned transfers without letting stale
  session cleanup release a replacement session's slot.
- Club buff selection uses the client `clubbuff.xml` mapping, validates established
  membership and leader authority, persists the selector, and transfers it between
  World and Game servers. Effects are recomputed after entry, departure, membership,
  and selection changes, including revocation when a member is no longer eligible.

### Metadata

- Feature/locale-filtered client `Xml.m2d` `table/constants.xml` values are merged with `Server.m2d` overrides into one required typed `ConstantsTable`, then validated and stored.
- Parser 2.4.24 uses read-only archive streams, including Linux crypto/NIF fixes
  absent from 2.3.7. The self-contained ingest image no longer mounts the checkout
  writable. Full read-only ingestion retains 28,620 NIFs and 235,082 map entities;
  failures reading geometry now stop ingestion instead of omitting failed models.
- Fishing lure metadata retains all 57 effect/level definitions across 55 effect
  codes. AI Battle/BattleEnd wrappers and raw numeric dungeon-mission operands are
  adapted to the current parser without inventing new runtime feature behavior.
- Database target validation precedes migrations and rejects metadata/player schema
  overlap or system schemas. Connection strings use escaped values; transient
  schema-read errors no longer authorize metadata recreation.
- The merged typed `ConstantsTable` is canonical for parsed constants. `Constant` is intended for code-owned emulator invariants; remaining exceptions are listed below.
- Item-option probability, selection, and variation data are joined from the server tables. Verified source coverage is 5,696 category-matched weights plus four entries that provide explicit random weights without probability rows; all 5,700 referenced variation entries resolve.
- Skill metadata includes a focused correction that enables the existing `useItem` behavior for premium potion `90000409` level 1; no separate consumption handler was added.
- Quest metadata retains raw repeat/period, faction, mission-rank, reputation-grade,
  `useMainFamePoint`, and `fameLog` values rather than omitting or reinterpreting them.
- The unused `Maple2.Graphics.Interface` project, hard-coded base-stat fallback/reset path, dead gifting helper, and unused generated-proto imports were removed.

## Quest lifecycle and reward boundaries

Client expiry lists are reconciliation requests, not deletion authority. The server
checks the persisted activation and its deadline before acknowledging expiry.
Expired active quests become inactive without losing their completion count or
tracking preference. Reacceptance uses the same atomic quest/item transaction.
Completed quests cannot expire or be abandoned, stale saves cannot resurrect an
expired activation or overwrite a newer start, and a pending guild-reward receipt
protects its activation from expiry.

The inspected NA/Live archive contains 4,903 quests. `repeatable` has values 0–3;
`usePeriod` includes empty, `5`, `1440`, `10080`, and `fri`. Controlled observations
with the existing v12 client establish active-quest expiry:

- Numeric periods are minutes from `StartTime`, including nonrepeatable quests.
  Two five-minute controls first requested expiry at exactly 300 seconds.
  Daily controls distinguish 24 elapsed hours from merely crossing midnight.
- Weekly `repeatable=3/usePeriod=fri` controls expire at the next Friday midnight
  in server UTC: a Thursday 23:59 start expired, while Friday 00:01 did not.
- A future `EndTime` did not prevent an old active quest from expiring. Completed
  controls did not request expiry, including old five-minute and daily records.

These rules now drive server-side expiry at loading, acceptance, completion, and
client reconciliation. Blank/unknown periods are not guessed. **Completed-quest
replay eligibility is a separate unresolved contract**; public replay remains
gated, while trusted server/admin restarts retain the complete acceptance path.

The five Alliance weapon-test quests `93000123`–`93000127` have acceptance items
and summoned portals. The acceptance transaction also covers the eight furnishing
rewards in seven housing quests. Existing item objects retain their identities
when stacks grow; all committed additions are applied before item-condition
notifications can consume or grant other items.

Sixty source quests define guild EXP/fund rewards; `73000001`–`73000004` specify
120 EXP and 20,000 funds each. Internal requests contain quest identity, not reward
amounts. A receipt identifies owner, quest, acceptance timestamp, and completion
count. Guild credit and its receipt are atomic, but World guild credit and Game
personal XP/currency/item rewards are **not a distributed transaction**.

Faction requirements with nonzero `fameGrade` fail closed while reputation
ownership and awards are unknown. Source `useMainFamePoint` values 1 and 2 are
preserved as integers; `fameLog=5000` is not guessed to mean 5,000 points or a
journal entry. The available fame-log table contains IDs 1–369. Locale-filtered
grade thresholds exist, but do not establish point awards or account/character
ownership. Alliance enum values are not cast to the different reputation wire IDs.
The full reputation subsystem remains unimplemented.

## Known blockers and data limits

- Maid and party-summon v12 packet flows do not have a reliable confirmed payload sequence.
- CharacterAbility data exists, but the NA/Live client data disables the feature.
- Henesys cannon interaction in [#334](https://github.com/MS2Community/Maple2/issues/334) still lacks a confirmed interaction payload/handler. Bomb pickup is a separate, implemented fix.
- [#668](https://github.com/MS2Community/Maple2/issues/668) (inaccessible quest enemies) and [#673](https://github.com/MS2Community/Maple2/issues/673) (login error 10053) need current-client reproduction; their historical reports do not establish a shared cause.
- The camera visibility reset for [#671](https://github.com/MS2Community/Maple2/issues/671) is implemented through existing packets; the complete cutscene still needs in-client confirmation.
- Meret-market gifting remains unsupported. The unused/commented gifting helper was removed rather than retaining a nonfunctional compatibility path.
- Item-option value weighting is not complete for the 6,306 IDs described above; this is a source-data limitation, not equivalent server probability data.
- Rested EXP credit is fixed, but the `restExp.xml` accumulation time unit and exact official offline/home timing remain unverified.
- Reverse-engineered packet structures must be confirmed against the client before adding fields or enabling incomplete flows.
- Faction acceptance has an NPC request path with explicit failure replies and
  atomic acceptance rewards. Active expiry clocks are client-observed; reputation
  awards/state and completed-quest replay eligibility remain unresolved.
- The existing client patch prints missing-XML diagnostics for some references
  absent from the original archive, including the nine paths in the investigated
  screenshot. Those messages still occur with successful login/world entry and
  are not evidence that resetting the server database will help. Its Winsock
  `10035` message describes an asynchronous connection still in progress.
- Encounter reports are not attributed to a generic trigger fix without evidence.
  The reviewed carrier, NPC-task, physical-jump, and summon paths are tracked
  separately in the issue ledger; source command counts are not simultaneous spawn counts.
- AI summon references in the inspected Barkhant/Surnuny data use small per-parent
  identifiers rather than loadable NPC template IDs. Their authoritative mapping
  was not present in the inspected NPC definitions or server tables. Summons remain
  disabled rather than guessing child IDs, count semantics, or living timeouts.
- Physical AI jumping remains unimplemented. The source speed/height parameters
  have not been tied to the client packet's angle/scale fields or a verified
  trajectory/navigation contract. An inferred implementation was rejected rather
  than shipping mismatched movement or leaving canceled NPCs suspended off-navmesh.

## Canonical state and retained data policies

Old dictionary-backed constants, reflected mutable constants, optional compatibility loaders,
hard-coded base-stat fallback, and the unused graphics-interface project are removed.
Application processes load local environment files once outside containers; containers use
their supplied environment.

The repository-wide cleanup also removes the orphan translation loader/CSV,
unreachable debug windows/shaders, obsolete movement routines, and unused generic
serialization helpers. EF Plus bulk deletes are replaced by EF's native
`ExecuteDelete`; DebugGame references the Silk input/windowing bindings it uses
instead of the umbrella package. Necessary migrations and active rendering
pipelines remain intact.

### Audit resolution overview

| Priority | Confirmed problem | Implemented resolution |
|---|---|---|
| 1 | Login responses could be reset; recreated Game endpoints left no playable channel | Graceful TCP close, fresh channel transports/monitors, stable endpoint registrations |
| 2 | Session, item, mail, trade, and market writes could partially commit or overwrite a new owner | Explicit transactions, checked saves, version/ownership guards, confirmed receipts, quarantine |
| 3 | Shop quantities and GameMeret debits were inconsistent | Checked stack totals, matching buyback prices, correct currency wallet |
| 4 | Implicit registration and Debug authentication/privilege shortcuts | Explicit validated registration and the same BCrypt authentication in every build |
| 5 | Ingestion and published assets depended on writable source paths | Read-only parser/archives, schema guards, self-contained ingestion, retained asset volumes |
| 6 | Unused translation data, debug code, utility methods, and broad dependencies remained | Delete unreachable code/data; use native EF deletes and specific Silk bindings |

This ranking describes the confirmed audit findings, not proof that the emulator
has no further defects. The protocol and source-data limitations above remain
explicitly outside the implemented coverage.

Code-owned NPC pursuit defaults and trigger reward aliases remain intentional server policies
where a verified replacement source is not available. Client-defined item variation ranges
remain necessary where server distributions are absent; they are not claimed to reproduce
missing retail probabilities.

`20260906125000_AccountWideDungeonRecords` adds explicit ownership scope to dungeon-record
keys and preserves character references. Its transactional old-row conversion protects
existing progress during rollout; it is a data migration, not an alternate supported API.
Do not remove it before all supported databases have completed that conversion.

`20260907103000_GuildQuestRewards` adds the durable guild-reward receipt ledger
without rewriting existing quests or guilds. Its receipts must survive retries and
ordinary quest progression; do not clear them to work around a rejected reward.

## Metadata refresh

Metadata-model, parser, typed-constants, room, or item-option table changes require re-ingestion. This updates metadata and applies migrations; it does not require deleting the MySQL volume or resetting players.

```powershell
.\scripts\stop_servers.ps1 -Service world,login,web,game-ch0,game-ch1
docker compose --profile ingest run --build --rm file-ingest
pwsh .\scripts\start_servers.ps1
```

Rebuild application images when source or parser versions changed; starting old images against
new metadata is not supported. For local development, run `dotnet tool restore` and then
`dotnet run --project Maple2.File.Ingest --`. Do not use `--drop-data` as a routine refresh command.

## Read-only trigger archive coverage

The continuation pass parses all 4,584 normalized trigger definitions from the
available NA/Live client data and verifies that every operation name is registered.
It also checks predicate/effect/transition preservation for 265 composite groups
across 105 trigger scripts. This establishes parsing and binding coverage, not
completion of every trigger operation's gameplay behavior.

## Club protocol evidence

The club selection request and existing response commands are corroborated by the
v12 [outbound club decoder](https://github.com/kOchirasu/MapleShark2-Scripts/blob/a1ff30dadb279c6db68cae7576a2fcd117abf08d/Outbound/0x0096.py)
and [inbound club decoder](https://github.com/kOchirasu/MapleShark2-Scripts/blob/a1ff30dadb279c6db68cae7576a2fcd117abf08d/Inbound/0x00F8.py).
Response commands 13 and 22 carry club ID, selector ID, and the observed level value
1; byte-layout tests cover both. The current metadata permits selectors 1–3,
mapped to their level-1 effects. No fee or unknown fields were invented.

Leader-only selection is enforced as server policy and is corroborated by the
leader receipt flow; no captured non-leader attempt was available. In-client
confirmation remains distinct from this source/protocol evidence.

## Docker build context

The Docker context excludes local `.env`, MCP configuration, agent-local settings, user web data, build outputs, packet captures, and other workstation artifacts. `LICENSE` remains included in application image build contexts.
