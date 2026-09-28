# SECOND LIFE *(working title)* — Port Arden

A persistent, systemic open-world life simulation set in **Port Arden**, a fictional Gulf Coast Texas metropolis.
Live any life — work, business, family, crime, politics — in a city that keeps living without you. Very rarely,
something extraordinary happens: resonance events that leave a few people with abilities nobody chose.

> **Working title only.** "Second Life" is a registered trademark of Linden Research, Inc. and must be replaced before
> any public release. The code name is `HeroGame`.

**Engine:** Unity 6 LTS (HDRP) · **Simulation:** engine-agnostic C# core (runs in Unity, a headless server, and CI) ·
**Art pipeline:** Blender 4.2 + Python automation + MCP command surface

## Status
Every simulation system is built and tested headlessly. That covers:

- **Economy and property:** economy, NPC lives, property and building, businesses, banking.
- **Crime and emergencies:** crime and courts, emergencies, combat, street crime, destruction.
- **Powers and civic life:** superpowers, elections and city government, social feed, radio, weather and disasters.
- **Story Mode.**
- **Multiplayer:** crash-safe saves, a TLS multiplayer server with accounts and moderation, world replication.
- **The city:** the full 19-district metro, about 50,000 residents.

**Tests:** 253 + 6 tests, green CI, a 128-player load test.

**Unity:** the Unity side (movement, driving, UI Toolkit HUD, phone and menus, combat, build mode, audio, online
client) compiles cleanly but **has not yet been confirmed running in a real editor**. See
**[docs/STATUS.md](docs/STATUS.md)** for the honest per-system status.

**What's missing for a finished game:** art, animation, voice acting, recorded music and play-testing.

Reports: [001 foundation](docs/reports/DEV_REPORT_001_FOUNDATION.md) · [002 systems](docs/reports/DEV_REPORT_002_SYSTEMS.md) ·
[003 world](docs/reports/DEV_REPORT_003_WORLD_AND_POLISH.md) · [004 combat/UI/performance](docs/reports/DEV_REPORT_004_COMBAT_UI_PERFORMANCE.md).

## Play it in Unity (first time)
1. Install **Unity 6 (6000.0.x)** with Unity Hub.
2. Open the **`Game/`** folder, not the repository root, and let packages import.
3. Check the scripts compiled: a **HeroGame** menu appears in the top menu bar.
   - If it doesn't, open *Window ▸ General ▸ Console* and look for red errors.
4. Run **HeroGame ▸ Setup ▸ Configure Project Settings**. Restart Unity if it asks.
5. Run *Window ▸ Rendering ▸ HDRP Wizard ▸ **Fix All***.
6. Run **HeroGame ▸ Health Check (run me first)**.
   - It checks the version, HDRP, input, data, a simulation smoke test and the scenes.
   - It writes `Logs/HeroGame_HealthCheck.txt` and copies the report to your clipboard.
7. If the Health Check says scenes are missing, run **HeroGame ▸ Build Main Menu Scene** and **HeroGame ▸ Build
   Greybox Vertical Slice**.
8. Open `Assets/_Project/Scenes/MainMenu.unity` and press **Play**.
   - Create a character, then **Continue** (or open `VerticalSlice_Greybox` directly).
   - The Console should print a line starting with `[Bootstrap] Port Arden`.
9. For the whole city, run **HeroGame ▸ Build Greybox Full Metro (heavy)**. It creates about 7,500 buildings.

### Controls
| Action | Keyboard / mouse | Gamepad |
|---|---|---|
| Move · look · sprint · jump · crouch | WASD · mouse · Shift · Space · Ctrl | left stick · right stick · L3 · A · B |
| Interact | E | X |
| Phone | ↑ | D-pad up |
| Pause & settings | Esc | Start |
| Enter / exit vehicle · horn | F · H | Y · L3 |
| Attack · next weapon | left mouse · X | RB · D-pad down |
| Powers: select, then hold to charge | 1–4, then hold Q | D-pad left/right, then RT |
| Build mode (inside a property you own) | B | Select |
| Dev console · world inspector | ` · F3 | — |

Dev console (`` ` ``) highlights: `help`, `money 5000`, `give compact_pistol`, `permit`, `mugging`, `storm Nadia 3 2`,
`fire`, `days 3`, `spawncar`, `own nearest`.

### Host a server
```bash
cd Headless && dotnet build HeroGame.Server -c Release
S=HeroGame.Server/bin/Release/net8.0/herogame-server
$S --save ./saves/server --dev-secret $(openssl rand -base64 32) --layout layout_port_arden.json
```
The server prints its TLS fingerprint.

## Documents
| | |
|---|---|
| [Technical Design Document](docs/TECHNICAL_DESIGN.md) | engine, architecture, data, networking, persistence, security, testing, performance targets |
| [Game Design Bible](docs/GAME_DESIGN_BIBLE.md) | the city, history, institutions, anomaly canon, characters, Story Mode structure |
| [Vertical Slice Plan](docs/VERTICAL_SLICE_PLAN.md) | milestones VS-0…VS-8 with acceptance criteria |
| [Asset Pipeline](docs/ASSET_PIPELINE.md) · [Asset Tracker](docs/ASSET_TRACKER.md) | Blender → Unity contract, placeholders |
| [Coding Standards](docs/CODING_STANDARDS.md) · [Build & CI](docs/BUILD_AND_CI.md) | conventions, how to build/run |

## Quick start
```bash
# Simulation + tests (needs .NET 8)
dotnet test Headless/HeroGame.sln

# Run a persistent world headless
cd Headless && dotnet build HeroGame.WorldHost -c Release
H=HeroGame.WorldHost/bin/Release/net8.0/herogame-world
$H new --save ./saves/dev && $H run --save ./saves/dev --days 30
$H inspect --save ./saves/dev business
$H inspect --save ./saves/dev npc Navarro

# Asset pipeline (needs Python 3.11)
pip install bpy==4.2.0
python Tools/Blender/cli.py kit --art-root Game/Assets/_Project/Art
```
Unity: open `Game/` with Unity 6 (6000.0.x) and follow [Build & CI › Unity — first open](docs/BUILD_AND_CI.md).

## Repository layout
```
Game/            Unity project — Assets/_Project/{Core,Persistence,Runtime,Runtime.Input,Editor,Tests}
                 and Assets/StreamingAssets/Data (all data-driven content)
Headless/        .NET solution that compiles the same Core/Persistence/Test sources + world host + Unity compile check
Tools/Blender/   asset pipeline (validation, LODs, collision, export, procedural kit, MCP commands)
Tools/Unity/     repository tooling (.meta integrity)
docs/            design documents, status, reports
```

![Procedural environment kit preview](docs/images/blender_kit_preview.png)
*First-pass procedural kit rendered headlessly with Cycles — blockout quality, not final art.*
