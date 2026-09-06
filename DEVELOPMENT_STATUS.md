# Development Status

This document describes the current implementation. It does not imply that every upstream issue is fixed.

[UPSTREAM_ISSUES.csv](UPSTREAM_ISSUES.csv) contains all 85 issues open upstream at the
2026-09-06 review snapshot, with per-issue implementation and evidence status.
An upstream issue remaining open does not establish that its original defect is
still present in this fork.

## Validated integration baseline (2026-09-06)

The following checks completed against isolated real client metadata and an isolated database:

- All 11 solution projects, including `Maple2.Server.DebugGame`, compile.
- The regular NUnit suite passes: 273 tests. Three additional explicitly selected
  MySQL persistence tests pass in a throwaway game database.
- Full ingestion completed all metadata and processed 1,183 maps and 235,082 map entities.
- SQL verification confirmed merged client/server constants, client pet slots, premium potion `90000409` `useItem`, 20 Toxic Garden weapons, 11 Henesys bombs, and Frey recovery metadata.
- The Docker World, Login, Web, `game-ch0`, and `game-ch1` stack starts successfully. Both Game health endpoints report `Healthy`, and Login plus both Game client ports return 25-byte handshakes.
- The refreshed service images shut down with exit code 0. MySQL has a one-minute
  stop grace period so volume flushing is not cut short by Docker's default timeout.
- The account-wide dungeon migration completed an upgrade, rollback, and re-upgrade
  against a separate disposable MySQL schema.

This is an integration baseline, not evidence that every gameplay issue or reverse-engineered protocol flow is complete.

## Implemented in this fork

### Runtime and operations

- Docker Compose keeps the custom two-channel topology: `game-ch0` is instanced content and `game-ch1` is the normal channel.
- MySQL, World, Login, and Game readiness checks gate startup. World, Login, and Game mount `config.yaml` read-only.
- Metadata ingestion is an explicit Compose profile and uses the repository-local EF Core 7.0.20 tool manifest.
- GitHub test and format workflows target .NET 8, use least-privilege read permissions, support fork pull requests, and never auto-commit.
- Login and Game session registries are concurrent and reconnect-safe. Shutdown stops listeners, notifies active/connecting clients, drains session sends, and disposes fields after disconnect begins.
- Missing user-stat metadata now fails explicitly. The obsolete level/job fallback and its hard-coded base-stat table were removed.

### Combat and items

- `damage start [object-id]`, `damage show`, and `damage stop` measure observed player, pet, and damage-over-time hits without changing combat.
- Enchant stat increases accumulate across enchant levels. Pre-enchanted generated gear receives the cumulative deltas for its current enchant level.
- Random item-option selection uses `Server.m2d` category weights, excludes explicit zero-probability options, preserves locked attributes, and prevents duplicate attribute lines.
- Server value distributions are used where present. Coverage is partial: 6,306 item-option IDs have client-defined value ranges but no server value distribution, so those values continue to use the existing range-based selection with a runtime warning.

### Progression and content

- Rested EXP is credited to total EXP and consumed using the typed canonical constant rate. The accumulation time unit in the available `restExp.xml` data is not established, so official home/offline accrual is not claimed as complete.
- Quest trigger detection handles requested quest states, job filtering, negation, and persisted exploration progress.
- Dungeon mission scoring, weekly rank-reward persistence, atomic mail delivery, and stale-week cleanup are implemented.
- Weekly rank cleanup preserves claims made in the current week, including delayed
  or repeated reset callbacks; the scheduled boundary is Friday midnight.
- Dungeon records distinguish account-wide and character-wide ownership. Existing character records are transactionally merged when an account-wide record is initialized.
- Colosseum rounds use parsed gear-score requirements and round rewards. Hidden-result clears still run completion logic without showing the result UI.
- Random-room metadata drives room selection, duration, portal model, capacity, and auto-close. A probability roll runs on the first active field tick; a recurring spawn interval is not established by the available data.
- Room admission counts loading clients as well as present players. Session-owned
  reservations are released on failed/abandoned transfers without letting stale
  session cleanup release a replacement session's slot.

### Metadata

- Feature/locale-filtered client `Xml.m2d` `table/constants.xml` values are merged with `Server.m2d` overrides into one required typed `ConstantsTable`, then validated and stored.
- The merged typed `ConstantsTable` is canonical for parsed constants. `Constant` is intended for code-owned emulator invariants; remaining exceptions are listed below.
- Item-option probability, selection, and variation data are joined from the server tables. Verified source coverage is 5,696 category-matched weights plus four entries that provide explicit random weights without probability rows; all 5,700 referenced variation entries resolve.
- Skill metadata includes a focused correction that enables the existing `useItem` behavior for premium potion `90000409` level 1; no separate consumption handler was added.
- The unused `Maple2.Graphics.Interface` project, hard-coded base-stat fallback/reset path, dead gifting helper, and unused generated-proto imports were removed.

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

## Canonical state and retained data policies

Old dictionary-backed constants, reflected mutable constants, optional compatibility loaders,
hard-coded base-stat fallback, and the unused graphics-interface project are removed.
Application processes load local environment files once outside containers; containers use
their supplied environment.

Code-owned NPC pursuit defaults and trigger reward aliases remain intentional server policies
where a verified replacement source is not available. Client-defined item variation ranges
remain necessary where server distributions are absent; they are not claimed to reproduce
missing retail probabilities.

`20260906125000_AccountWideDungeonRecords` adds explicit ownership scope to dungeon-record
keys and preserves character references. Its transactional old-row conversion protects
existing progress during rollout; it is a data migration, not an alternate supported API.
Do not remove it before all supported databases have completed that conversion.

## Metadata refresh

Metadata-model, parser, typed-constants, room, or item-option table changes require re-ingestion. This updates metadata and applies migrations; it does not require deleting the MySQL volume or resetting players.

```bash
docker compose stop world login web game-ch0 game-ch1
docker compose --profile ingest run --build --rm file-ingest
pwsh ./scripts/start_servers.ps1
```

Rebuild application images when source or parser versions changed; starting old images against
new metadata is not supported. For local development, run `dotnet tool restore` and then
`dotnet run --project Maple2.File.Ingest --`. Do not use `--drop-data` as a routine refresh command.

## Docker build context

The Docker context excludes local `.env`, MCP configuration, agent-local settings, user web data, build outputs, packet captures, and other workstation artifacts. `LICENSE` remains included in application image build contexts.
