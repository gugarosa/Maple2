# MapleStory2 Server Emulator

This is an open source MapleStory2 server emulation project written in C#. It includes multiple services (world, login, game channels, web) and a MySQL database, with Docker-based development and runtime support.

## Overview

- Services:
  - World: global coordination and metadata services.
  - Login: authentication and session handoff.
  - Game: one or more channel servers; `game-ch0` handles instanced content.
  - Web: optional web utilities and diagnostics.
  - MySQL: persistent database (Docker volume).
- Code layout highlights:
  - `Maple2.Server.World`, `Maple2.Server.Login`, `Maple2.Server.Game`, `Maple2.Server.Web`.
  - `Maple2.Model`, `Maple2.Database`, `Maple2.File.Ingest` for shared model/data tooling.
  - `compose.yml` defines services; `scripts/start_servers.ps1` orchestrates startup for dev.

## Quick Start (Docker)

Prerequisites:
- Docker Desktop (Compose v2 recommended)
- Windows PowerShell or pwsh (the helper script is PowerShell-based)

1) Clone the repo and create a `.env` file at the repo root:

```
# Required
DB_PASSWORD=yourStrongPassword

# Recommended
GAME_IP=192.168.1.100   # your host LAN IP for client handoff
MS2_DOCKER_DATA_FOLDER=C:\\Path\\To\\ClientData  # for file-ingest
```

2) Start everything (builds images and starts DB → world/login/web → game):

```
pwsh ./scripts/start_servers.ps1
```

3) Tail logs:

```
docker compose logs -f world login game-ch0 game-ch1
```

4) Stop and remove containers:

```
docker compose down
```

Notes:
- Ports (host → container):
  - Web: 80/443/4000
  - Login: 20001 (and healthcheck at 21000)
  - World: 21001
  - Game ch0: 20002, 21002; Game ch1: 20003, 21003
- Data is persisted in a named Docker volume `mysql`.
  Use `docker compose down -v` to reset the database (destructive).

## Dev Workflow: Game-Only Rebuild/Restart

The helper script supports a game-only mode to speed up iteration:

- Rebuild and restart only game servers (instanced + ch1 by default):

```
pwsh ./scripts/start_servers.ps1 -GameOnly
```

- Specific channels and no instanced content:

```
pwsh ./scripts/start_servers.ps1 -GameOnly -IncludeInstanced:$false -NonInstancedChannels 1,2
```

- Restart without rebuilding:

```
pwsh ./scripts/start_servers.ps1 -GameOnly -NoBuild
```

In game-only mode the script uses `docker compose up --no-deps --force-recreate` for the game services (and `--build` if supported) to avoid touching DB/world/login.

## Server Configuration

Use `config.yaml` in the repo root (or set `CONFIG_PATH`). All keys are optional.

- Exp: `exp.global`, `exp.kill`, `exp.quest`, `exp.dungeon`, `exp.prestige`, `exp.mastery`
- Loot: `loot.global_drop_rate`, `loot.boss_drop_rate`, `loot.rare_drop_rate`
- Mesos: `mesos.drop_rate`, `mesos.per_level_min`, `mesos.per_level_max`
- Difficulty: `difficulty.damage_dealt_rate`, `difficulty.damage_taken_rate`, `difficulty.enemy_hp_scale`, `difficulty.enemy_level_offset`

Example `config.yaml` snippet:

```
exp:
  global: 2.0
  kill: 2.0
  quest: 1.5
loot:
  global_drop_rate: 1.5
mesos:
  drop_rate: 2.0
  per_level_min: 1.0
  per_level_max: 3.0
```
