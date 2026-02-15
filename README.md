# MapleStory2 Server Emulator

An open-source MapleStory2 server emulator written in C# (.NET 8.0). Run your own MapleStory2 server with a distributed microservices architecture, Docker support, and configurable game settings.

[![Build](https://github.com/MS2Community/Maple2/actions/workflows/build.yml/badge.svg)](https://github.com/MS2Community/Maple2/actions/workflows/build.yml)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](https://www.gnu.org/licenses/agpl-3.0)
[![Discord](https://img.shields.io/discord/PLACEHOLDER?label=Discord&logo=discord)](https://discord.gg/r78CXkUmuj)

---

## Prerequisites

You will need:

- A **MapleStory2 client** installation (provides game data files)
- **Docker Desktop** with Compose v2 (recommended) — _or_ .NET 8.0 SDK + MySQL 8.0 for local development
- **PowerShell** (Windows PowerShell or [pwsh](https://github.com/PowerShell/PowerShell))

## Quick Start (Docker)

### 1. Clone and configure

```bash
git clone https://github.com/MS2Community/Maple2.git
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
docker compose run file-ingest
```

### 3. Generate navmeshes

Navmeshes enable NPC pathfinding and movement. Without them, maps load but NPCs stand still.

```bash
docker compose run file-ingest -- --run-navmesh
```

> **Note:** This processes all maps with walkable surfaces and can take a while on the first run. Subsequent runs skip maps that haven't changed.

### 4. Start the servers

```bash
pwsh ./scripts/start_servers.ps1
```

This builds all Docker images and starts services in order: MySQL → World → Login/Web → Game channels.

### 5. Connect

Point your MapleStory2 client at `127.0.0.1` (or your `GAME_IP`) port `20001`.

### Managing the servers

```bash
# Tail logs
docker compose logs -f world login game-ch0 game-ch1

# Stop all services
docker compose down

# Reset database (destructive — deletes all player data)
docker compose down -v
```

## Quick Start (Local / No Docker)

If you prefer running without Docker:

```powershell
# Interactive setup — checks .NET, installs dotnet-ef, downloads server files, imports game data
.\setup.bat

# Start all servers (World + Login + Web + Game) in separate windows
.\start.bat

# Or dev mode — World + Login + Web only (no game channel)
.\dev.bat
```

This requires .NET 8.0 SDK and a local MySQL 8.0 instance.

## Architecture

```
┌─────────┐     ┌─────────┐     ┌───────────┐     ┌───────────┐
│  Client  │────▸│  Login  │────▸│   World   │◂───▸│   MySQL   │
│          │     │ :20001  │     │  :21001   │     │  :3306    │
└────┬─────┘     └─────────┘     └─────┬─────┘     └───────────┘
     │                                 │ gRPC
     │           ┌─────────────────────┼─────────────────────┐
     │           │                     │                     │
     ▼           ▼                     ▼                     ▼
┌──────────┐ ┌──────────┐        ┌──────────┐         ┌──────────┐
│ Game Ch0 │ │ Game Ch1 │  ...   │ Game ChN │         │   Web    │
│ :20002   │ │ :20003   │        │          │         │  :4000   │
│(instanced│ │          │        │          │         │          │
│ content) │ │          │        │          │         │          │
└──────────┘ └──────────┘        └──────────┘         └──────────┘
```

| Service | Description |
|---------|-------------|
| **World** | Central coordinator — manages global state (guilds, parties, player info) via gRPC |
| **Login** | Handles authentication, character selection, and server list |
| **Game** | Runs actual gameplay. Multiple channel instances per world. `game-ch0` handles instanced content (dungeons) |
| **Web** | Web-based APIs and utilities |
| **MySQL** | Persistent storage for player data and game metadata |

Inter-server communication uses **gRPC (HTTP/2)**. Client connections use a **custom TCP protocol** with MapleCipher encryption.

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
├── compose.yml                # Docker Compose service definitions
├── config.yaml                # Game server tuning (exp rates, drop rates, etc.)
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

# Custom channels — e.g. channels 1 and 2, skip instanced
pwsh ./scripts/start_servers.ps1 -GameOnly -NoInstanced -NonInstancedChannels 1,2
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
dotnet format whitespace --verify-no-changes --exclude 'Maple2.Server.World/Migrations/*.cs'

# Apply formatting fixes
dotnet format whitespace --exclude 'Maple2.Server.World/Migrations/*.cs'
```

### Database migrations

```bash
# Install EF Core tools (if not already installed)
dotnet tool install --global dotnet-ef

# Create a new migration
dotnet ef migrations add <MigrationName> --project Maple2.Server.World

# Apply pending migrations
dotnet ef database update --project Maple2.Server.World
```

## Configuration

### Environment variables (`.env`)

Copy `.env.example` to `.env` and edit. Key variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `MS2_DATA_FOLDER` | Path to MS2 client `Data/` directory (local) | — |
| `MS2_DOCKER_DATA_FOLDER` | Same, but for Docker volume mount | — |
| `DB_IP`, `DB_PORT`, `DB_USER`, `DB_PASSWORD` | MySQL connection | `localhost:3306` / `root` |
| `DATA_DB_NAME` | Database for game metadata | `maple-data` |
| `GAME_DB_NAME` | Database for player data | `game-server` |
| `GAME_IP`, `LOGIN_IP` | IPs the client connects to | `127.0.0.1` |
| `GRPC_WORLD_IP`, `GRPC_WORLD_PORT` | World server gRPC endpoint | `127.0.0.1:21001` |
| `LANGUAGE` | Primary language (`EN`, `KR`, `CN`, `JP`, `DE`, `PR`) | `EN` |

### Game tuning (`config.yaml`)

All values are multipliers (1.0 = default). Drop this file in the repo root:

```yaml
exp:
  global: 2.0        # Multiplier for all XP sources
  kill: 1.0
  quest: 1.5

loot:
  global_drop_rate: 1.5
  boss_drop_rate: 2.0
  rare_drop_rate: 1.0

mesos:
  drop_rate: 2.0
  per_level_min: 1.0
  per_level_max: 3.0

mob:
  damage_dealt_rate: 1.0     # Player damage multiplier
  damage_taken_rate: 1.0     # Incoming damage multiplier
  enemy_hp_scale: 1.0
  enemy_level_offset: 0
```

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
- **Wiki**: [Setup Guide](https://github.com/MS2Community/Maple2/wiki/Prerequisites) · [Understanding Packets](https://github.com/MS2Community/Maple2/wiki/Understanding-packets) · [Packet Resolver](https://github.com/MS2Community/Maple2/wiki/Packet-Resolver)

## License

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE).
