# SECOND LIFE *(working title)* — Port Arden

A persistent, systemic open-world life simulation set in **Port Arden**, a fictional Gulf Coast Texas metropolis.
Live any life — work, business, family, crime, politics — in a city that keeps living without you. Very rarely,
something extraordinary happens: resonance events that leave a few people with abilities nobody chose.

> **Working title only.** "Second Life" is a registered trademark of Linden Research, Inc. and must be replaced before
> any public release. The code name is `HeroGame`.

**Engine:** Unity 6 LTS (HDRP) · **Simulation:** engine-agnostic C# core (runs in Unity, a headless server, and CI) ·
**Art pipeline:** Blender 4.2 + Python automation + MCP command surface

## Status
Phase 0 (foundation) is done: architecture, design documents, a tested simulation core, persistence with crash
recovery, a headless world host, Unity runtime/editor code (compile-verified, not yet run in the editor) and a working
Blender pipeline. See **[docs/STATUS.md](docs/STATUS.md)** for the honest per-system status and
**[docs/reports/DEV_REPORT_001_FOUNDATION.md](docs/reports/DEV_REPORT_001_FOUNDATION.md)** for measurements, known
issues and the next phase.

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
