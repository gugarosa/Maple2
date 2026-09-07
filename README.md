# MapleStory2 Server Emulator

An open-source MapleStory2 server emulator written in C# (.NET 8.0). Run your own MapleStory2 server with a distributed microservices architecture, Docker support, and configurable game settings.

[![Tests](https://github.com/gugarosa/Maple2/actions/workflows/test.yml/badge.svg)](https://github.com/gugarosa/Maple2/actions/workflows/test.yml)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)

---

## Prerequisites

You will need:

- A **MapleStory2 client** installation (provides game data files)
- **Docker Engine or Docker Desktop** with Compose v2 (recommended) — _or_ the .NET 8 SDK, the `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` 8.x runtimes, and MySQL 8 for local development
- **PowerShell** (Windows PowerShell or [pwsh](https://github.com/PowerShell/PowerShell))

Check local runtime availability with `dotnet --list-runtimes`. The repository targets .NET 8; a newer SDK alone is not a substitute for the required 8.x shared runtimes.

`global.json` selects a stable .NET 8 SDK so local builds and GitHub Actions use
the same formatter/toolchain generation. Install the current .NET 8 SDK, even if
a newer major SDK is already installed.

## Quick Start (Docker)

### 1. Clone and configure

```bash
git clone https://github.com/gugarosa/Maple2.git
cd Maple2
cp .env.example .env
```

Edit `.env` with your settings:

```env
# Required
DB_PASSWORD=yourStrongPassword

# Path to your MapleStory2 client Data folder (for importing game data)
MS2_DOCKER_DATA_FOLDER=C:/Nexon/Library/maplestory2/Data/

# Your host/LAN IP — clients use this to connect to game channels
# Use 127.0.0.1 for local play only
GAME_IP=192.168.1.100
```

### 2. Import game data

This reads your MapleStory2 client files and populates the database with game metadata (items, NPCs, maps, quests, etc.):

```bash
docker compose --profile ingest run --build --rm file-ingest
```

The ingest service is opt-in and does not run during `docker compose up`. It restores the repository-local EF Core 7.0.20 tool, applies game-database migrations, and updates metadata by checksum.

Re-run ingestion after parser, constants, or metadata-model changes. Stop application services first so they do not retain stale metadata caches; keep MySQL running and never delete its volume for a metadata refresh:

```bash
docker compose stop world login web game-ch0 game-ch1
docker compose --profile ingest run --build --rm file-ingest
pwsh ./scripts/start_servers.ps1
```

`--drop-data` recreates the metadata database and is intentionally omitted from normal setup. Back up data and understand the impact before using it.

### 3. Generate navmeshes

Navmeshes enable NPC pathfinding and movement. Without them, maps load but NPCs stand still.

```bash
docker compose --profile ingest run --build --rm file-ingest -- --run-navmesh
```

> **Note:** This processes all maps with walkable surfaces and can take a while on the first run. Subsequent runs skip maps that haven't changed.

### 4. Start the servers

```bash
pwsh ./scripts/start_servers.ps1
```

This builds the application images and starts services in order: MySQL → World → Login/Web → Game channels.

### 5. Connect

Point your MapleStory2 client at `127.0.0.1` (or your `GAME_IP`) port `20001`.

### Managing the servers

```bash
# Tail logs
docker compose logs -f world login game-ch0 game-ch1

# Stop all services
docker compose down
```

Do not use `docker compose down -v` unless you intentionally want to delete the persistent MySQL volume, including player data.

## Quick Start (Local / No Docker)

If you prefer running without Docker:

```powershell
# Interactive setup — checks .NET, restores pinned tools, downloads server files, imports game data
.\setup.bat

# Start all servers (World + Login + Web + Game) in separate windows
.\start.bat

# Or dev mode — World + Login + Web only (no game channel)
.\dev.bat
```

This requires the .NET 8 SDK and 8.x shared runtimes plus a local MySQL 8 instance. `setup.ps1` uses `.config/dotnet-tools.json`; it does not install or replace global EF tools.

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
| **Web** | Web-based APIs and utilities |
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
pwsh ./scripts/start_servers.ps1 -GameOnly

# Restart without rebuilding (if only config changed)
pwsh ./scripts/start_servers.ps1 -GameOnly -NoBuild

# Restart only the configured normal channel
pwsh ./scripts/start_servers.ps1 -GameOnly -NoInstanced -NonInstancedChannels 1
```

> **Important:** After a game channel restart, connected clients must re-login from the login screen.

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
and club buff persistence
against MySQL. It creates a uniquely named temporary game database and deletes only
that database afterward. It does not load connection settings from `.env`.

Set `DB_IP`, `DB_PORT`, `DB_USER`, and `DB_PASSWORD` for an isolated MySQL instance,
and point `DATA_DB_NAME` to an ingested database whose name starts with
`maple2_validation_`. Then run:

```powershell
$env:MAPLE2_RUN_DB_TESTS = "1"
dotnet test Maple2.Server.Tests\Maple2.Server.Tests.csproj --filter "FullyQualifiedName~GameStoragePersistenceTests"
```

These tests are explicitly selected, not run against normal development or player
databases by the default test command.

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
| `GRPC_WORLD_IP`, `GRPC_WORLD_PORT` | World server gRPC endpoint | `127.0.0.1:21001` |
| `LANGUAGE` | Primary language (`EN`, `KR`, `CN`, `JP`, `DE`, `PR`) | `EN` |

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

## Community

- **Discord**: [Join the server](https://discord.gg/r78CXkUmuj)
- **Fork issues**: [gugarosa/Maple2](https://github.com/gugarosa/Maple2/issues)
- **Upstream wiki**: [Setup Guide](https://github.com/MS2Community/Maple2/wiki/Prerequisites) · [Understanding Packets](https://github.com/MS2Community/Maple2/wiki/Understanding-packets) · [Packet Resolver](https://github.com/MS2Community/Maple2/wiki/Packet-Resolver)

## License

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE).
