# MapleStory2 Server Emulator

An open-source MapleStory2 server emulator written in C# (.NET 8.0). Run your own MapleStory2 server with a distributed microservices architecture, Docker support, and configurable game settings.

[![Tests](https://github.com/gugarosa/Maple2/actions/workflows/test.yml/badge.svg)](https://github.com/gugarosa/Maple2/actions/workflows/test.yml)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

Quest expiry is checked against server time and retains completion history.
Replaying completed quests and reputation-grade prerequisites remain gated until
their separate eligibility/state contracts are verified; see
[Development Status](DEVELOPMENT_STATUS.md#quest-lifecycle-and-reward-boundaries).

---

## Prerequisites

You will need:

- A **MapleStory2 client** installation (provides game data files)
- **Docker Engine or Docker Desktop** with Compose **2.20 or newer** (recommended) — _or_ the .NET 8 SDK, the `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` 8.x runtimes, and MySQL 8 for local development
- **PowerShell 5.1 or newer** (Windows PowerShell or [pwsh](https://github.com/PowerShell/PowerShell))

Check local runtime availability with `dotnet --list-runtimes`. The repository targets .NET 8; a newer SDK alone is not a substitute for the required 8.x shared runtimes.

`global.json` selects a stable .NET 8 SDK so local builds and GitHub Actions use
the same formatter/toolchain generation. Local development requires .NET 8 even
if a newer major SDK is installed; the Docker images include their own toolchain.

## Quick Start (Docker)

### 1. Clone and configure

```powershell
git clone https://github.com/gugarosa/Maple2.git server
New-Item -ItemType Directory -Path client, release
Set-Location server
Copy-Item .env.example .env
```

Keep the original game installation in sibling `client`, this checkout in
`server`, and generated distribution material in `release`. Mushroom Launcher
keeps its normal per-user installation outside these folders. See
[Client Setup](CLIENT_SETUP.md) for the exact file, launcher, and release contract.

Edit `.env` with your settings:

```env
# Required
DB_PASSWORD=yourStrongPassword

# Path to your MapleStory2 client Data folder (for importing game data)
MS2_DOCKER_DATA_FOLDER=D:\MapleStory\MapleStory2\client\Data

# Local play
CLIENT_BIND_IP=127.0.0.1
GAME_IP=127.0.0.1
LOGIN_IP=127.0.0.1
```

For LAN clients, set `CLIENT_BIND_IP=0.0.0.0` and set both `GAME_IP` and `LOGIN_IP`
to your server's LAN address. Database, gRPC, and Web host ports remain
loopback-only. Public registration requires an HTTPS reverse proxy.

**Existing installation:** preserve its Compose project name and MySQL volume.
If moving or renaming the checkout, set `COMPOSE_PROJECT_NAME` in `.env` to the
existing project name. Changing that name selects a different volume; it does not
migrate your characters.

Compose retains three named volumes: `mysql` for databases, `web-data` for uploaded
images/designs, and `navmeshes` for generated navigation. Back up all three before
major updates. If an older installation kept Web uploads only inside its container,
copy those files into `web-data` before removing that container.

### 2. Import game data

This reads your MapleStory2 client files and populates the database with game metadata (items, NPCs, maps, quests, etc.):

```bash
docker compose --profile ingest run --build --rm file-ingest
```

The ingest service is opt-in and does not run during `docker compose up`. Its image
contains the source and pinned tools, applies game-database migrations, and updates
metadata by checksum. Client archives are mounted read-only; the checkout is not
mounted into the ingest container. Supply all original `.m2d`/`.m2h` archive pairs
and the customized `Server.m2d`/`Server.m2h` before starting.

Metadata and player database names must be different. Ingestion rejects overlapping
or system database targets before running migrations. Connection values are escaped,
including passwords containing connection-string punctuation.

Full geometry ingestion needs several GiB of memory. It was validated with an
8 GiB container allowance; a 3 GiB limit was insufficient. Archive/decryption or
out-of-memory failures are errors, not a successful partial import.

Re-run ingestion after parser, constants, or metadata-model changes. Stop application services first so they do not retain stale metadata caches; keep MySQL running and never delete its volume for a metadata refresh:

```powershell
.\scripts\stop_servers.ps1 -Service world,login,web,game-ch0,game-ch1
docker compose --profile ingest run --build --rm file-ingest
pwsh .\scripts\start_servers.ps1
```

`--drop-data` recreates the metadata database and is intentionally omitted from normal setup. Back up data and understand the impact before using it.

### 3. Generate navmeshes

Navmeshes enable NPC pathfinding and movement. Without them, maps load but NPCs stand still.

```bash
docker compose --profile ingest run --build --rm file-ingest --run-navmesh
```

> **Note:** This processes all maps with walkable surfaces and can take a while on the first run. Subsequent runs skip maps that haven't changed.

Generated files and their hashes are stored in the `navmeshes` volume. Both Game
channels mount it read-only; generating navigation no longer depends on modifying
the source checkout or rebuilding Game images afterward. Stop application services
as above before generating navigation, then start them again: Game caches loaded
navigation and must restart to use newly generated files.

### 4. Start the servers

```bash
pwsh .\scripts\start_servers.ps1
```

This builds images before changing containers, then starts MySQL → World →
Login/Web → instanced Game → normal Game with bounded readiness checks. Build,
startup, or readiness errors stop the script instead of reporting success.

### 5. Register and sign in

Open `http://localhost:4000/account` and register a username and password.
Usernames use 3-24 letters, numbers, or underscores. Passwords use 8-16 characters
to fit the existing client's password field. Registration is explicit: an unknown
or blank login never creates an account.

Install [official Mushroom Launcher](https://github.com/shuabritze/mushroom-launcher/releases/tag/v2.0.3),
close it, then configure the existing client:

```powershell
.\scripts\configure_client.ps1 -ClientPath "D:\MapleStory\MapleStory2\client"
```

Open `client\Mushroom Launcher.lnk`. The profile connects to Login at `127.0.0.1`
port `20001`; for another machine pass the operator's Login host using
`-LoginHost`. Sign in with your registered credentials. Leave automatic login disabled
to type them in the client; keep the client's default local/locale login mode
enabled. The optional debug console is not required for normal play.

If a compatible client is already patched but its launcher shortcut is missing,
launch it from PowerShell using its own installation as the working directory:

```powershell
$clientPath = "C:\Games\MapleStory2\client"
Start-Process -FilePath "$clientPath\x64\MapleStory2.exe" -WorkingDirectory $clientPath `
    -ArgumentList "--nxapp=nxl", "--ip=127.0.0.1", "--port=20001"
```

The legacy client's password field can display its contents. Do not share
screenshots of populated login fields; use a compatible launcher's masked
credential prompt/profile when needed. For public registration, put the Web
service behind HTTPS rather than exposing the HTTP form directly.

Existing accounts and characters are preserved. New registrations have no
administrator permissions or development currency grants. Empty development
credentials are no longer accepted.

### Managing the servers

```bash
# Tail logs
docker compose logs -f world login game-ch0 game-ch1

# Stop all services, retaining containers and all three data volumes
pwsh .\scripts\stop_servers.ps1
```

Do not use `docker compose down -v` for routine shutdown or updates: it deletes
player databases, uploaded designs, and generated navigation.

## Local debugging (advanced)

Normal play uses the Docker scripts above. For native debugging, install MySQL 8
and the .NET 8 SDK/runtimes, configure `.env`, and stop application services first:

```powershell
# Validate local settings/archives and ingest metadata; no downloads or prompts
.\setup.bat
```

Launch projects under your debugger in World, Login/Web, instanced Game, normal
Game order. Use `--instanced` for the instanced Game process and leave
`INSTANCED_CONTENT=false` for the normal process. `setup.ps1` validates inputs and
uses repository-local EF tools; it never downloads or overwrites client archives.
The obsolete `start.bat` and `dev.bat` window launchers have been removed.

## Architecture

```
Client ──TCP──▶ Login :20001 ──gRPC──▶ World :21001
   │                                      ▲
   ├──TCP──▶ Game ch0 :20002 ──gRPC───────┤
   └──TCP──▶ Game ch1 :20003 ──gRPC───────┘

World, Login, Game, and Web :4000 ──▶ MySQL :3306
```

| Service | Description |
|---------|-------------|
| **World** | Central coordinator — manages global state (guilds, parties, player info) via gRPC |
| **Login** | Handles authentication, character selection, and server list |
| **Game** | Runs actual gameplay. Multiple channel instances per world. `game-ch0` handles instanced content (dungeons) |
| **Web** | Account registration and client APIs for uploaded images/designs and rankings |
| **MySQL** | Persistent storage for player data and game metadata |

Inter-server communication uses **gRPC (HTTP/2)**. Client connections use a **custom TCP protocol** with MapleCipher encryption.

See [DEVELOPMENT_STATUS.md](DEVELOPMENT_STATUS.md) for the validated build/test/ingestion/Docker baseline, implemented systems, and current protocol/data limitations. [UPSTREAM_ISSUES.csv](UPSTREAM_ISSUES.csv) records the complete reviewed issue inventory and distinguishes implemented changes, prior mitigations, unconfirmed reports, and blockers. Neither document claims that every gameplay flow is complete.

## Project Structure

```
Maple2/
├── Maple2.Server.World/       # World server (gRPC coordinator)
├── Maple2.Server.Login/       # Login server
├── Maple2.Server.Game/        # Game channel server
│   └── Navmeshes/             # NPC pathfinding data (generated)
├── Maple2.Server.Web/         # Web server
├── Maple2.Server.Core/        # Shared networking, packet handling, encryption
├── Maple2.Database/           # Entity Framework Core data layer (MySQL)
├── Maple2.Model/              # Shared data models, enums, metadata
├── Maple2.Tools/              # Utility libraries (DotRecast, extensions)
├── Maple2.File.Ingest/        # Imports game data from MS2 client files
├── Maple2.Server.Tests/       # NUnit test suite
├── Maple2.Server.DebugGame/   # Debug/development game server
├── scripts/                   # Docker orchestration scripts
├── .config/dotnet-tools.json  # Repository-local EF Core 7 tool
├── compose.yml                # Docker Compose service definitions
├── config.yaml                # Game server tuning (exp rates, drop rates, etc.)
├── DEVELOPMENT_STATUS.md      # Current implementation and known limitations
├── UPSTREAM_ISSUES.csv         # Dated upstream issue reconciliation
└── .env                       # Environment configuration (DB, IPs, paths)
```

## Development Workflow

### Rebuilding game channels only

When iterating on game logic, you don't need to restart the database or world server:

```bash
# Rebuild and restart game channels (keeps world/login/web running)
pwsh .\scripts\start_servers.ps1 -GameOnly

# Restart without rebuilding (if only config changed)
pwsh .\scripts\start_servers.ps1 -GameOnly -NoBuild

# Restart only the configured normal channel
pwsh .\scripts\start_servers.ps1 -GameOnly -NoInstanced -NonInstancedChannels 1
```

> **Important:** After a game channel restart, connected clients must re-login from the login screen.

`-GameOnly` requires healthy MySQL, World, Login, and Web services before making
changes. Use a full startup when shared services, authentication, or registration
code changes; a Game-only rebuild cannot update World/Login/Web.

### Building and testing

```bash
# Build entire solution
dotnet build

# Build a specific project (faster iteration)
dotnet build Maple2.Server.Game/Maple2.Server.Game.csproj

# Run tests
dotnet test

# Check code formatting
dotnet format whitespace --verify-no-changes --exclude 'Maple2.Server.World\Migrations'

# Apply formatting fixes
dotnet format whitespace --exclude 'Maple2.Server.World\Migrations'
```

The GitHub test and format workflows are read-only validations. They use .NET 8 and do not commit formatting changes back to contributor branches.

`.editorconfig` defines C# whitespace and UTF-8 BOM conventions; `.gitattributes`
keeps C# checkout line endings consistent across workstations and CI.

### Opt-in persistence tests

`GameStoragePersistenceTests` exercises account-wide migration, rank-mail transactions,
club persistence, registration, atomic quest acceptance, expiry, and stale-save
protection against MySQL. The other explicit fixtures cover guild reward receipts,
item/mail/trade ownership, black-market transactions, and transaction failure
handling. Each creates a uniquely named temporary game database and deletes only
that database afterward. They do not load connection settings from `.env`.

Set `DB_IP`, `DB_PORT`, `DB_USER`, and `DB_PASSWORD` for an isolated MySQL instance,
and point `DATA_DB_NAME` to an ingested database whose name starts with
`maple2_validation_`. Then run:

```powershell
$env:MAPLE2_RUN_DB_TESTS = "1"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~GameStoragePersistenceTests"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~GuildQuestRewardPersistenceTests"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~ItemTransferPersistenceTests"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~BlackMarketPersistenceTests"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~DatabaseRequestPersistenceTests"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~ResetPersistenceTests"
```

These tests are explicitly selected, not run against normal development or player
databases by the default test command.
They apply the actual game migrations so account, character, and mail identifiers
use the same separate ranges as a normal installation.

The dependency-free operations checks exercise script argument validation,
startup ordering, failure propagation, and volume-safe shutdown without running
Docker or touching real data:

```powershell
.\scripts\test_operations.ps1
.\scripts\test_client_setup.ps1
```

Create a revision-stamped source-only client setup kit with
`.\scripts\build_client_release.ps1` from a clean checkout. It goes to sibling
`release`; no proprietary client, launcher binary, credentials, or user data is
packaged. [Client Setup](CLIENT_SETUP.md#build-and-distribute-the-setup-kit)
documents the distribution boundaries and remaining remote-release gates.

### Read-only client trigger validation

With a compatible client installed, this explicit test imports its trigger metadata
in memory and checks runtime parsing, operation mappings, and composite conditions.
It does not change client files or connect to a database.

```powershell
$env:MS2_DATA_FOLDER = "C:\Path\To\MapleStory2\Data"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~CompositeTriggerArchiveTests"
```

Re-ingest metadata after updating trigger normalization so persisted scripts use the
same canonical representation.

The same read-only approach covers event carrier patrols and combat-exit AI:

```powershell
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~EventPatrolArchiveTests"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~AiLifecycleArchiveTests"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~QuestLifecycleArchiveTests"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~ParserArchiveCompatibilityTests"
```

### Measuring damage in game

Use `damage start` to measure attacks against the nearest living NPC or dummy, or
`damage start <object-id>` to choose a target. `damage show` reports observed
minimum/maximum hit damage, total damage, DPS, critical hits, misses, and blocks.
`damage stop` freezes the measurement. Player skill, pet, and damage-over-time
hits are included; the command does not simulate attacks or alter combat stats.

### Database migrations

```bash
# Restore the repository-local EF Core 7 tool
dotnet tool restore

# Create a new migration
dotnet ef migrations add <MigrationName> --project Maple2.Server.World

# Apply pending migrations
dotnet ef database update --project Maple2.Server.World
```

The tool manifest pins EF 7.0.20 to match this repository's EF 7 projects. Do not replace unrelated global `dotnet-ef` installations.

## Configuration

### Environment variables (`.env`)

Copy `.env.example` to `.env` and edit. Key variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `MS2_DATA_FOLDER` | Path to MS2 client `Data/` directory (local) | — |
| `MS2_DOCKER_DATA_FOLDER` | Same, but for Docker volume mount | — |
| `DB_IP`, `DB_PORT`, `DB_USER`, `DB_PASSWORD` | MySQL connection (`DB_PASSWORD` is required) | `localhost:3306` / `root` |
| `DATA_DB_NAME` | Database for game metadata | `maple-data` |
| `GAME_DB_NAME` | Database for player data | `game-server` |
| `GAME_IP`, `LOGIN_IP` | IPs the client connects to | `127.0.0.1` |
| `CLIENT_BIND_IP` | Host interface exposing Login and Game client ports | `127.0.0.1` |
| `COMPOSE_PROJECT_NAME` | Preserve the existing project name when moving a checkout | Checkout directory name |
| `GRPC_WORLD_IP`, `GRPC_WORLD_PORT` | World server gRPC endpoint | `127.0.0.1:21001` |
| `LANGUAGE` | Primary language (`EN`, `KR`, `CN`, `JP`, `DE`, `PR`) | `EN` |

The Compose file sets `WEB_DATA_DIR=/app/Data` and `MS2_NAVMESH_DIR=/app/Navmeshes`
for its persistent asset volumes. Native development resolves assets in the
checkout; published services resolve them beside the application unless an
explicit asset-directory override is supplied.

All five service entrypoints apply environment overrides after `appsettings.json`.
For example, `Serilog__WriteTo__0__Args__restrictedToMinimumLevel=Verbose` enables
the existing console packet trace in a Debug build. Restart the affected service
after changing an environment setting. Login credentials and authentication-key
packets are excluded from packet traces.

### Game tuning (`config.yaml`)

The repository mounts `config.yaml` read-only into World, Login, and both Game containers. The file is the authoritative list of EXP, loot, meso, mob, and despawn settings. Most rates are multipliers (`1.0` is neutral); `enemy_level_offset` is an integer and despawn caps are seconds (`0` disables the cap).

### Network ports

| Service | Client Port | gRPC/Internal Port |
|---------|------------:|-------------------:|
| Login   | 20001       | 21000              |
| World   | —           | 21001              |
| Game ch0| 20002       | 21002              |
| Game ch1| 20003       | 21003              |
| Web     | 4000        | —                  |
| MySQL   | 3306        | —                  |

## Troubleshooting the client

| Symptom | What to check |
|---------|---------------|
| Login stays on a loading screen | Check Login and World logs as well as Game health. World must advertise an active **normal** channel; a listening port alone does not prove the login handoff works. Rebuild outdated images after updating server code. |
| Client console says connection error `10035` | This is `WSAEWOULDBLOCK`, the pending result of a nonblocking connection. The existing client patch labels it as a failure; check whether the client subsequently connects before treating it as a server outage. |
| Client console says `Failed to load XML file` | This is emitted by the client patch, not metadata ingestion. Some referenced files are absent from the original archive. Do not create empty replacement XML files or reset the player database to suppress the message. Report the exact path and affected gameplay. |
| Client cannot find its data | Confirm both data-folder settings point to the actual current installation, especially after moving it to another directory. |
| Correct password works in a launcher but not when typed in the client | The legacy password field truncates after 16 characters. New registrations enforce that limit; existing longer credentials can still be supplied by a compatible launcher without truncation. |
| Registration returns `400`, `409`, or `429` | Correct the form errors, choose an unused username, or wait a minute after repeated attempts. Reload the page after a Web restart to obtain a fresh form token. |

## Community

- **Discord**: [Join the server](https://discord.gg/r78CXkUmuj)
- **Fork issues**: [gugarosa/Maple2](https://github.com/gugarosa/Maple2/issues)
- **Upstream wiki**: [Setup Guide](https://github.com/MS2Community/Maple2/wiki/Prerequisites) · [Understanding Packets](https://github.com/MS2Community/Maple2/wiki/Understanding-packets) · [Packet Resolver](https://github.com/MS2Community/Maple2/wiki/Packet-Resolver)

## License

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE).
