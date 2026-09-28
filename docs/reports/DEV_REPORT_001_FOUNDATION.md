# Development Report 001 — Phase 0: Foundation

**Scope:** GDD §186 "First task" (Technical Design Document → Game Design Bible → Vertical Slice Plan → begin
implementation) and Phase 0 (project foundation), plus the engine-agnostic simulation core that later phases build on.

**Environment constraints (stated plainly):** development ran in a Linux container **without a Unity editor or GPU**.
Therefore:
* Everything engine-agnostic (simulation, persistence, content, tests, world host) was built, **executed and tested**
  with .NET 8.
* Unity scripts (runtime + editor) were **compile-verified** against Unity reference assemblies from NuGet
  (UnityEngine 2021.3 modules, UnityEditor 2021.1) in editor and player configurations. They have **not** been run
  inside Unity. The Input System adapter could not be compile-checked (package not available offline).
* The Blender pipeline ran headless via the official `bpy` 4.2 module: generation, validation, LODs, collision, FBX
  export and FBX re-import were executed and tested; a Cycles preview was rendered.

## 1. Completed systems
| Area | Delivered |
|---|---|
| Documentation | TDD, Game Design Bible (original city, lore, cast, story structure), Vertical Slice Plan, live STATUS board, asset pipeline, asset/placeholder tracker, coding standards, build & CI guide |
| Project foundation | Unity 6 project (`Game/`), package manifest (HDRP, Input System, Addressables, AI Navigation, Timeline, Test Framework, Newtonsoft), 6 assembly definitions with enforced boundaries, deterministic `.meta` GUIDs, LFS rules, editorconfig |
| Simulation core | stable ids, deterministic RNG, world clock, event bus, server config + integrity clamps, double-entry ledger, atomic journaled transactions, loans, taxes, macro economy, geography & data-driven layout expansion, property service, business simulator, NPC generator/schedules/life sim/social sim/director, weather & tropical systems, crime/witness/evidence/wanted, secret identity, reputation, modular powers (composition, complexity-driven rarity, anomaly events, discovery, progression, interactions), server history & news, server browser, moderation |
| Persistence | JSON contract, content loader + cross-reference validation, generation-based incremental snapshots, write-ahead journal with torn-tail repair, integrity gate, story slots, account profiles |
| Headless | `herogame-world` (new / run / resume / inspect / bench), text world/NPC/economy/business/history/news inspectors |
| Unity runtime (compile-checked) | bootstrap & session with autosave, third-person motor/camera/animator bridge, input abstraction + Input System adapter, interaction system (doors, property purchase, business counters), place markers, day/night (solar model), weather presentation, cell streamer, NPC materialisation with pooling, main menu / server browser / character creator (UI Toolkit), prototype HUD, dev console + world/NPC inspector (dev builds only) |
| Unity editor (compile-checked) | greybox scene builder driven by layout data, main-menu builder, model import rules (UCX colliders, LOD groups, texture rules), project setup, validators, CI build entry points |
| Asset pipeline | conventions, validation, LOD/collision automation, Unity-contract FBX export with reports, procedural kit (15 assets), MCP command surface, CLI |
| CI | .NET build/tests/compile-check/smoke/bench; Blender tests + kit artifact; meta integrity; GameCI workflow gated on license secrets |

## 2. Test results
* **63/63** NUnit tests passing (`dotnet test Headless/HeroGame.sln`) — the same sources Unity's Test Runner will run.
* **9/9** Blender pipeline tests passing (5 pure-Python, 4 with `bpy`).
* Unity compile check: editor configuration ✔, player configuration ✔ (warnings treated as errors).

Highlights: offline catch-up produces a world identical to live simulation over 20 days; one-step vs. per-day NPC
catch-up is identical; purchases are atomic under stale ownership, insufficient funds and journal failure; a crash after
a purchase is recovered from the journal and a retried request is rejected by its idempotency key; a torn journal tail
is repaired; saving refuses a ledger whose balances do not sum to zero; time/space powers are >15× rarer than body
enhancement; a second power occurs < 1/300 even at the epicentre of a maximal event.

## 3. Performance measurements (container CPU, Release build)
| Measurement | Result |
|---|---|
| Generate vertical slice (≈305 NPCs, 131 households, 20 businesses, 111 properties) | ~80 ms |
| Simulate 365 days, vertical slice | 148 ms total (0.4 ms/day) |
| Generate stress layout (9,967 NPCs, 4,448 places) | 310–330 ms |
| NPC background simulation | **3.4–4.4 µs per NPC-day** (≈170–220 ms per game day for 50k NPCs) |
| Population director (8 observers, 4,448 places) | 6.0–6.5 ms per evaluation ⚠ |
| Full save, 10k NPCs (JSON, 18 MB) | 1.5–1.8 s ⚠ |
| Incremental save after one day, 10k NPCs | 0.6–1.0 s ⚠ (daily sim dirties population) |
| Load + journal replay + invariant check, 10k NPCs | ~1.1 s |
| Managed memory, 10k-NPC world | 34 MiB |
| Blender kit: 15 assets generated, validated, exported | ~9 s |

## 4. Balance issues found and fixed during this phase
Found by running a simulated year and inspecting the output, not by guessing:
1. Business cycle drifted into its clamps (0.6/1.4) → retuned the AR(2) cycle (stays within ~0.72–1.13 over 30 years).
2. Unemployment converged to ~0% (job finding 7/yr vs. separations ~4%/yr) → calibrated to ~4–5%.
3. B2B logistics revenue was scaled by pedestrian traffic → per-template `FootTrafficSensitivity`.
4. Fuel stations could never profit (COGS 86% + opex 18%) → per-template `OperatingExpenseRatio`.
5. Business margins without an opex line were unrealistically high (diner ~36%) → operating expenses added.
6. Name generator could produce "Harper Harper" → first name never equals surname.
7. Admins could not appoint moderators (missing permission) → fixed, covered by test.

## 5. Known bugs / unverified areas
* **Unity has never opened this project.** First open may surface: package resolution (versions pinned for 6000.0.23f1),
  HDRP default settings, UI Toolkit theme warnings (runtime-created `PanelSettings` without a theme), physics layers
  for the camera collision mask, gamepad look sensitivity being frame-rate dependent.
* `WorldStreamer` has no authored cell scenes yet; it currently treats missing cells as empty.
* NPC avatars only appear for outdoor activities (commuting, parks) — indoor materialisation arrives with interiors.
* `SocialSimulator` couples move into one partner's household without checking home capacity.
* `LoanBook.ProcessDuePayments` catches up missed months in a loop when a server was offline; with long downtime a
  borrower can default in a single catch-up (arguably correct, but should notify the player on return).
* Weather after very long downtime only simulates the last 14 days hour-by-hour (by design; documented).

## 6. Assets created
* Procedural environment kit (blockout quality): `SM_Storefront_2F_A`, `SM_Storefront_2F_B`, `SM_Storefront_3F_C`,
  `SM_Office_8F_A`, `SM_Warehouse_A`, `SM_House_Shotgun_A/B/C`, `SM_Apartment_4F_A`, `SM_StreetLight_A`, `SM_Bench_A`,
  `SM_FireHydrant_A`, `SM_Bollard_A`, `SM_BusShelter_A`, `SM_Dumpster_A` — each with 3–4 LODs, convex UCX collision,
  named PBR material slots, UV0 + lightmap UVs. Exported by CI as an artifact (not committed: binary files go through
  Git LFS, which is not configured in this environment).
* Content data: 61 occupations, 18 business templates, 22 power archetypes, 8 anomaly causes, 23 interaction rules,
  26 crime types, name pools, vertical-slice and stress layouts, default server config, mock server list.
* Preview render: `docs/images/blender_kit_preview.png`.

## 7. Assets still required (next milestones)
Humanoid player/NPC characters with a locomotion animation set; modular NPC appearance system; kit art pass (trim sheets,
textures, signage); interiors (corner store, diner, house, apartment, warehouse shell); vehicles (1 car, 1 police car);
terrain/water; VFX (rain, anomaly phenomena); UI art; audio ambience.

## 8. Technical debt
| # | Debt | Plan |
|---|---|---|
| 1 | JSON saves are large and slow for big worlds; save runs on the main thread | MessagePack chunks behind the existing manifest; snapshot on main thread (copy), serialize on a worker |
| 2 | Population chunk is rewritten every game day | split population chunks by district and by "changed today" sets |
| 3 | Population director scans all places each evaluation | uniform spatial grid of places (64 m cells) |
| 4 | NPC household finances are aggregate numbers outside the ledger | closed-loop consumer spending pool feeding businesses (Phase 19) |
| 5 | Business demand ignores competition between nearby businesses | district demand pools shared by same-category businesses |
| 6 | `DevCommands` anomaly command temporarily swaps `World.OnlineCharacters` | explicit exposure API |
| 7 | UnityCompileCheck uses 2021.x reference assemblies | replace with Unity 6 reference assemblies from a licensed CI image when available |

## 9. Next recommended phase
**Phase 1 — Core character (VS-1)**, in this order:
1. Open in Unity 6, fix first-open issues, run EditMode tests in Unity, generate the greybox and main-menu scenes.
2. Play-test the motor/camera; tune; add a humanoid placeholder with a locomotion blend tree.
3. Import the Blender kit into the greybox (swap boxes for kit meshes by `PlaceKind`), bake NavMesh, verify NPCs on
   sidewalks.
4. Implement the population director spatial index (debt #3) before scaling density.
5. Begin Phase 3 interiors in parallel (corner store + diner) since NPC visibility depends on them.
