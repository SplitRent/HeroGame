# Technical Design Document

**Project:** SECOND LIFE *(working title — see §0)* · **Codebase:** `HeroGame` · **Doc version:** 0.1 (Phase 0) · **Owner:** lead development AI + project owner

This document defines the architecture everything else is built on. It is normative: code that
contradicts it is a bug, or the document gets amended in the same change. Status of every system lives in
[`STATUS.md`](STATUS.md); nothing here implies a feature is finished.

---

## 0. Working title and IP

* "Second Life" is a registered trademark of Linden Research, Inc. It is used **only** as the internal working
  title requested by the project owner. It exists in exactly one constant
  (`GameInfo.WorkingTitle`, `Runtime/Bootstrap/LaunchRequest.cs`) and must be replaced before any public build,
  store page, trailer or trademark filing.
* The code name `HeroGame` (repository name) is title-independent; namespaces and assemblies never contain the title.
* All world content (city, organisations, characters, brands, sports teams, music) is original. No third-party IP,
  trademarks, real brands or real people. Real-world places are inspirations only (Gulf Coast Texas), never reproduced.

## 1. Engine, versions and render pipeline

| Decision | Choice | Rationale |
|---|---|---|
| Engine | **Unity 6 LTS (6000.0.x)**, pinned in `Game/ProjectSettings/ProjectVersion.txt` (6000.0.23f1; move to the latest 6000.0 patch freely) | LTS stability for a multi-year project; dedicated-server build target; mature C# tooling |
| Scripting | C# 9 / .NET Standard 2.1 profile, IL2CPP for players, Mono for editor/server | Unity 6's supported language level; the headless build pins `LangVersion 9.0` so both compilers agree |
| Render pipeline | **HDRP 17** (Unity 6) | Photoreal target (volumetrics, physically based sky, SSR/SSGI, ray tracing option) for PC/console |
| Input | Input System 1.11 (actions defined in code) | Rebindable, gamepad-first, no device code in gameplay |
| UI | UI Toolkit (runtime) for front end, HUD, phone | Retained-mode, stylable, scales to complex menus (server browser, business management) |
| Navigation | AI Navigation 2.x (NavMesh) | Full-tier NPC and police pathing |
| Content delivery | Addressables 2.x | Streaming, patching, per-cell bundles |
| Cinematics | Timeline 1.8 | Reusable cutscene system (Phase 15) |

**HDRP risk:** Unity has announced that future renderer work consolidates around a unified pipeline, with HDRP
receiving maintenance rather than new features. Mitigations, enforced now:
1. No gameplay or simulation assembly references HDRP. Pipeline-specific code lives only in presentation
   components (e.g. `WeatherPresenter.Applied` hooks) and editor tooling.
2. Art is authored as standard metallic/roughness PBR (Blender Principled BSDF → HDRP/Lit), with texture naming that
   maps 1:1 to URP/Lit — content survives a pipeline switch.
3. Greybox materials resolve `HDRP/Lit → URP/Lit → Standard` at build time.

## 2. Architecture overview

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Unity client / Unity dedicated server (Game/)                                │
│  HeroGame.Runtime        MonoBehaviours: player, camera, interaction,        │
│                          NPC presentation, streaming, weather/day-night, UI  │
│  HeroGame.Runtime.Input  Input System adapter (only assembly touching devices)│
│  HeroGame.Editor         greybox builder, import rules, validation, CI builds │
├──────────────────────────────────────────────────────────────────────────────┤
│ Engine-agnostic simulation (compiled by Unity AND by .NET for server/tests)  │
│  HeroGame.Core           world model + systems (no UnityEngine reference)    │
│  HeroGame.Persistence    JSON, content loading, snapshots, journal           │
├──────────────────────────────────────────────────────────────────────────────┤
│ Headless (.NET 8, Headless/)                                                 │
│  HeroGame.WorldHost      authoritative world host: create/load, offline      │
│                          catch-up, inspection, benchmarks                    │
│  HeroGame.Tests          runs the Unity EditMode test sources under dotnet   │
│  UnityCompileCheck       compiles Runtime/Editor against Unity ref assemblies│
└──────────────────────────────────────────────────────────────────────────────┘
```

**Single source of truth.** Core, Persistence and the EditMode tests live once, under `Game/Assets/_Project`.
Unity compiles them through `.asmdef`s; the `.csproj` files in `Headless/` glob the same files. There is no copy step.

**Why engine-agnostic core.** The simulation (NPC lives, economy, businesses, weather, anomalies, crime) must run:
in the client (Story Mode), in a Unity dedicated server, in a headless world process (offline catch-up, admin
tooling, future sharding), and in CI in seconds. `HeroGame.Core.asmdef` sets `noEngineReferences: true`; the compiler
enforces the boundary.

### 2.1 Assembly rules
| Assembly | May reference | Must not reference |
|---|---|---|
| HeroGame.Core | nothing | UnityEngine, Newtonsoft, IO |
| HeroGame.Persistence | Core, Newtonsoft.Json | UnityEngine |
| HeroGame.Runtime | Core, Persistence, UnityEngine modules | Input System, HDRP |
| HeroGame.Runtime.Input | Runtime, Unity.InputSystem | — |
| HeroGame.Editor | all of the above, UnityEditor | shipped in players |

### 2.2 Communication
* **Events:** `EventBus` (typed pub/sub, synchronous, with deferred queue flushed once per simulation step).
* **Services:** constructor injection inside Core; `ServiceRegistry` (explicit, registered only by `GameBootstrap`)
  at the Unity seam. No hidden singletons in Core. No `FindObjectOfType` in gameplay code.
* **Data flow:** Unity reads world state and issues *requests* (buy property, use power, report crime). Requests are
  validated and applied by Core services. In multiplayer the request is an RPC to the server, which runs the same
  Core call; the client never mutates authoritative state.

## 3. Data architecture (GDD §82)

Five layers, never mixed:

| Layer | Type | Lifetime / owner | Stored |
|---|---|---|---|
| Global account | `AccountProfile`, `CharacterIdentity`, `AppearanceData` | account service | account DB (local `AccountStore` offline) |
| Server-specific character | `ServerCharacter` | one per (account, server) | world save, chunk `character/{id}` |
| World simulation | `World` (+ registries) | one per server / story slot | world save (chunks + journal) |
| Content | `ContentSet` (JSON) | shipped / modded data | `StreamingAssets/Data/*.json` |
| Session | runtime components, `GameSession` caches | process | never persisted |

* Money is **never** stored on characters: balances live in the ledger; `ServerCharacter` stores account ids.
* Ownership is **never** stored on assets: the `OwnershipRegistry` is the single source of truth.
* **Stable IDs:** `EntityId` = 8-bit kind + 56-bit sequence, allocated by the persisted `IdAllocator`, never reused.
  Text form `Npc:42` is used in saves and logs. Content uses string keys; runtime entities use `EntityId`.
* All simulation randomness uses `DeterministicRandom` (xoshiro256\*\*), keyed per purpose, e.g.
  `For(worldSeed, npcId, dayIndex)`. `System.Random`/`UnityEngine.Random` are banned in Core.

## 4. Time

* `GameDateTime` = integer seconds since 2000-01-01. `WorldClock` scales real time (default 48 real minutes per game
  day, server-configurable 10–1440).
* `WorldSimulation.Update()` advances hour by hour from the persisted cursor to the clock: hourly weather/outages/
  anomaly triggers; at midnight the *completed* day is simulated (economy, every NPC, social pass, businesses, loans,
  property, next day's anomaly roll).
* **The same code path runs live and during offline catch-up.** A server that restarts after 20 days produces a world
  bit-identical to one that ran live for 20 days (`OfflineCatchUp_MatchesLiveSimulation` test). Catch-up is capped at
  60 days per start-up.

## 5. NPC architecture (GDD §12–16, §86, §94–96)

### 5.1 Identity & persistence
`NpcRecord` holds identity, appearance seed, personality (Big Five), household, home, job, salary, education,
schedule inputs, finances, health, relationships, bounded memories of players (≤32, summarised scalars + flags) and a
bounded life history (≤24 events). NPCs are never regenerated; the generator runs once per world.

### 5.2 Simulation tiers
| Tier | Where | Cost |
|---|---|---|
| Background | everyone | daily aggregate life sim (~3.4 µs per NPC-day measured) + stateless schedule lookup |
| Nearby | within ~220 m of a player | pooled lightweight avatar, steering toward schedule position |
| Full | within ~60 m (budget 40) | NavMesh agent, perception, dialogue, combat/flee (Phase 3+) |

### 5.3 Where is Marcus right now?
`ScheduleResolver.Resolve(npc, time)` is a **pure function** of the NPC record and the time (randomness keyed by
world, NPC and day). Asking twice gives the same answer; asking after a restart gives the same answer. That makes
materialisation trivial and consistent: the `PopulationDirector` finds candidate NPCs through a place→NPC index (home,
work, school, favourite places near observers), resolves their schedules, and returns the nearest N with tiers.
Commuters are interpolated between origin and destination, which is what populates sidewalks. Indoor activities are
materialised by interiors, not on the street, so nobody spawns in front of the camera (§184).

### 5.4 Life simulation
`NpcLifeSimulator.SimulateDay`: mortality (Gompertz), health, life stages, layoffs/hiring/promotions driven by the
macro economy, aggregate finances, stress-linked criminal propensity. `SocialSimulator.DailyPass` handles multi-NPC
events (dating, marriage, divorce, births) in stable id order. History entries feed the server history/news.

### 5.5 Networking of NPCs
NPCs are **not** NetworkObjects. The server runs the director per player and sends each client compact NPC
snapshots (id, position, heading, activity, appearance seed) for its relevant set only; clients render them with the
same pooled avatars. Full-tier behaviour (combat, dialogue) is server-simulated and replicated as state changes.

## 6. Economy architecture (GDD §19, §23–24, §104, §173–177)

* **Double-entry ledger.** Every transaction's postings sum to zero, so the sum of all balances is always zero.
  `Ledger.VerifyInvariant` runs before every save; a broken invariant refuses to persist (duplication bugs cannot be
  saved). The `External` account represents the outside economy; its negated balance is the net money that entered
  the city, broken down by `TransactionReason` — currency generation is auditable.
* **Atomic world transactions.** `WorldTransaction` = ledger postings + ownership changes. `TransactionProcessor`:
  validate everything → append to the write-ahead journal (fsync) → apply in memory. There is no state where money moved
  and the asset did not (tests: `Purchase_FailsCleanly_*`, `Crash_AfterPurchase_IsRecoveredFromJournal`).
* **Idempotency keys** make client retries safe (a purchase request resent after a timeout never double-charges).
* **Sources.** Player/Admin transactions are journaled; Simulation transactions (business days, loan payments) are
  deterministic consequences of time and are recomputed after a crash instead of journaled.
* **Aggregate simulation.** Individual customers are not simulated. `BusinessSimulator` uses a demand curve: base
  customers × open hours × district foot traffic × reputation × price elasticity × weather sensitivity × weekday ×
  advertising × macro cycle, capped by staffing and inventory. Costs: COGS/restock, wages, fixed costs, operating
  expenses, business tax. Businesses that cannot pay lose staff and reputation — they never create money.
* **Macro economy:** mean-reverting business cycle with rare recessions, inflation/price level, unemployment; shocks
  from disasters. NPC household finances are aggregate numbers (not ledger accounts) to keep the ledger focused on
  player-relevant money; wages paid by player businesses flow to an external "households" sink.
* **Server configuration** can tune taxes, prices, wages and payouts inside validated bounds
  (`ServerConfigValidator`): no setting can make property free, multiple powers common, or money infinite.

## 7. Property & construction architecture (GDD §20–22, §72–76)

* `PropertyRecord` (address, kind, zoning, area, value, condition, damage state, security, tenancy, furniture,
  layout id). Ownership via registry; purchases via atomic transactions with transfer tax; weekly re-assessment from
  district wealth, condition, damage and macro state; daily wear.
* **Building system (Phase 5, planned):** room/wall graph per lot (`LayoutId`) rather than voxels. Walls on a 0.25 m
  grid snapped to lot bounds; openings are components on walls; rooms are derived polygons. Validation rules:
  structural (spans, load-bearing), zoning (use vs `ZoningType`), utilities, entrances, accessibility. Conversions
  (warehouse → nightclub) = change of use permit + layout edit + business template assignment.
* **Damage:** layered — per-object health, visual damage states, `DamageState` on property; expensive destruction only
  on authored/high-value structures (GDD §76).

## 8. Multiplayer, server and database architecture (GDD §84–89)

### 8.1 Topology (staged)
1. **Phase 9 — single authoritative server per world.** A Unity dedicated server (Linux headless build) hosts physics,
   movement, combat and replication; it embeds the Core `World` and `WorldSimulation`. Target 64–128 players.
2. **Phase 22+ — split simulation.** The Core world runs in a separate world-service process (the `WorldHost` is its
   prototype); one or more Unity "zone" servers handle physics per region and talk to it over gRPC. Players are handed
   off between zones at cell boundaries. Nothing in Core assumes a single process — this is why Core is engine-free.

### 8.2 Netcode
* **Netcode for GameObjects 2.x + Unity Transport (UDP, DTLS)**, server-authoritative. Gameplay code talks to an
  `INetSession` abstraction so the transport/library can be swapped (Fish-Networking is the evaluated fallback).
* Player movement: client-side prediction with server reconciliation (`PlayerMotor.Step` is deterministic for a given
  input + dt for this reason). Other entities: snapshot interpolation (100 ms buffer).
* **Interest management:** spatial hash grid (64 m cells); relevance by distance + priority (vehicles in pursuit,
  powers in use) with per-client bandwidth budgets. Distant objects are not replicated at all.
* Tick rates: simulation/physics 30 Hz server, snapshots 20 Hz (nearby) / 5 Hz (far relevant), world clock sync 1 Hz.

### 8.3 Server validation (never trust the client)
Movement speed/teleport checks against the motor's limits and the player's powers; inventory and money only change via
server Core calls; damage computed server-side; power use validated against `CharacterPowers` (stamina, cooldowns,
stage); vehicle ownership via registry; every transaction via the processor.

### 8.4 Services and databases
| Service | Tech | Storage |
|---|---|---|
| Account & identity | ASP.NET Core minimal API | PostgreSQL (`accounts`, `character_identity`) |
| Master server / browser | ASP.NET Core + heartbeats every 15 s | Redis (live listings) |
| World (per server) | Unity dedicated server + Core | **Self-hosted:** file snapshots + journal (implemented). **Official hosting:** PostgreSQL for transactional state (ledger, ownership, characters) + object storage for simulation chunks, behind the same `ITransactionJournal`/snapshot interfaces |

Community servers must be runnable by players on their own machines with no database install — hence file storage
is first-class, not a dev shortcut.

### 8.5 Moderation (GDD §89)
Role-based permissions (`ServerRole`, `ServerPermission`), rank rules (nobody moderates peers or superiors), append-only
moderation history, timed mutes/bans. Admin economy actions go through `World.AdminGrant` — journaled and attributable.

## 9. Persistence & save system (GDD §83, §172–174)

### 9.1 Format
JSON via Newtonsoft (`JsonSetup`: enums as strings, `EntityId` as `"Kind:Seq"`, money as integer cents,
`TypeNameHandling.None` — save files can never instantiate arbitrary types). A binary format (MessagePack) is planned for
large worlds (§14 tech debt); the chunk/manifest structure is format-independent.

### 9.2 Layout
```
<save>/manifest.json            commit record: generation + chunk → generation map + journal sequence (written last, atomically)
<save>/manifest.prev.json       the previous commit record, kept as a fallback (its chunks are kept too)
<save>/chunks/<chunk>.g<N>.json immutable chunk files: meta, transactional, population, properties,
                                businesses, environment, history, vehicles, justice, emergency, civic,
                                social, destruction, story (Story Mode only), population_<shard>,
                                layouts_<0..15>, character_<id>
<save>/journal.log              write-ahead journal (JSON lines, fsync per entry)
```

### 9.3 Incremental, crash-consistent snapshots
Only dirty chunks are rewritten (`DirtyTracker`); unchanged chunks keep their previous generation. Every file write is
atomic (temp + fsync + rename). Because the manifest is written last, a crash at any point leaves the previous
snapshot intact. `meta` and `transactional` (ledger + ownership + loans + journal sequence) are always written
together so they agree.

The manifest records the city layout (`LayoutId`, `LayoutFile`); a world only opens on its own layout
(`LayoutMismatchException` otherwise) and hosts reopen it on the recorded file without being told.

What blocks the simulation thread is kept small: population shards and households are captured as detached copies
(`NpcRecord.SnapshotCopy`, a member-wise copy of the nested lists; life events and power definitions are immutable and
shared) and serialized with the building layouts inside `Commit`, which a background save runs on another thread.
Building layouts are copy-on-write (a build replaces the object), so the 16 layout shards are rewritten only when a
layout reference changed since the shard was last written; older saves with layouts inline in `properties` still load.

### 9.4 Recovery
Load manifest → load chunks → replay journal entries with sequence > snapshot sequence (player/admin transactions) →
verify ledger invariant → resume simulation from the cursor (simulation transactions are recomputed). The journal
discards a torn final line (interrupted, never-acknowledged append) before appending; an unreadable entry in the middle
is skipped and reported (each entry is balanced on its own, so the books stay consistent).

If the latest snapshot is damaged after it was committed (unreadable manifest, missing or truncated chunk), load falls
back to `manifest.prev.json` and replays the journal from *its* sequence; the next save is then a full snapshot and the
good fallback is kept until that save succeeds. To make this possible the journal is compacted only up to the previous
snapshot's sequence, never the latest one.

Autosaves use `SaveInBackground`: the calling thread takes a consistent snapshot (builds and serializes the dirty
chunks), clears the dirty set and returns; writes, fsyncs, the manifest commit and garbage collection run on a background
thread. A failed background write puts its chunks back for the next save. Journal compaction waits for the task on the
simulation thread, which owns the journal. Explicit saves (quit, checkpoints, console) stay synchronous. Non-journaled simulation state (NPC lives, weather) rolls back by at most one
save interval in that case; money and ownership do not. Fault injection (`WorldSaveSystem.BeforeCommit`) lets tests cut
the power between chunk writes and the manifest commit (`FailureTests`).

### 9.5 Story Mode
Same `WorldSaveSystem` per slot: 10 manual slots, 3 rotating autosaves, 1 mission checkpoint (`StorySlotManager`).

### 9.6 Versioning
Every root has `SchemaVersion`. Loading a newer schema than the build supports is refused; older schemas migrate
through explicit, tested upgrade steps (none needed yet).

## 10. Gameplay systems architecture

### 10.1 Crime, evidence, police (GDD §28–32)
* `WitnessModel`: each observer (NPC/camera) → identification confidence from distance, visibility and concealment;
  report decision from crime report likelihood × district police trust × personal civic-mindedness.
* Evidence combines by noisy-OR into identification. Costumed crimes produce evidence against the **alias**, not the
  civilian — the secret-identity system is fed by evidence, never by fiat.
* `WantedSystem` phases: Clear → Investigating → Searching → Pursuit. Level (1–5) is *derived* from severity,
  repetition, pursuit, violence against police and powered status; unidentified suspects cannot exceed level 1 and
  cannot be pursued. Losing police = leaving a growing search radius; identified suspects get a warrant afterwards.
* Police AI (Phase 8): dispatcher assigns units to incidents by distance and level; units use NavMesh/traffic lanes;
  roadblocks and air units unlock by level.

### 10.2 Superpowers (GDD §39–47, §106–110)
* Powers are compositions of `PowerComponent`s (domain × verb × delivery × element + magnitude/range/precision/
  efficiency) plus limitations. Archetypes are JSON data; the runtime executes components, so new powers need no code.
* **Emergent rarity:** generation weight = event affinity × exp(−k × complexity). Measured with neutral events: body
  enhancement is >15× more common than time/space effects; ~3% of manifestations are novel archetype-free compositions.
* **Anomaly events** are rolled per day (default ≈1 per 45 days per city), placed in a district, flavoured by cause
  (refinery resonance, canal lightning, bayou fog bloom…), and expose everyone present — including NPCs.
* **Discovery:** Latent (incubation) → Manifesting (involuntary symptoms) → Aware (first deliberate success) →
  Practiced → Mastered; evolutions unlock with experience.
* **Multiple powers:** second ≈ 1 in 1,100 at the epicentre of a maximal event, third another ~1/100 on top; server
  multiplier hard-capped at 5×.
* **Interactions:** data-driven rule table (element × material tags → outcome, priority, tag changes).
* **Safety (§110):** power effects run through the physics layer with per-effect caps (mass, force, radius), server
  validation, and the damage system's recovery states; world-altering effects are bounded to their area and duration.

### 10.3 Vehicles (Phase 4, planned)
WheelCollider-based arcade-sim hybrid, server-authoritative with client prediction for the driver. Vehicle records
(ownership via registry, fuel, damage per component, mods, registration/insurance) persist in the world save.
Ambient traffic: lane graph from road layout data; far traffic simulated statistically per road segment and only
materialised beyond the camera frustum.

### 10.4 Weather & disasters
Seasonal Markov chain per hour + tropical systems in June–November announced 2–4 days before landfall; effects
(traction, visibility, pedestrian density, business demand, outage risk, emergency declarations) are consumed by
traffic, NPCs, businesses and emergency services.

Local disasters (`CalendarService`) are rolled from world state with deterministic per-district, per-hour random
streams: flash floods need heavy rain and scale with district flood risk (property damage, water rescues); chemical
incidents happen in industrial/port districts (a district closure that forces its businesses shut once it covers half
the trading day, hazmat calls, phone alerts); blackouts are likelier in storms and heat (outage hours cut business
hours); heat waves occur in hot summers (evening demand shifts, heat-exhaustion EMS calls among older residents). A
server multiplier (`DisasterFrequencyMultiplier`, 0 disables) scales them.

### 10.4.1 Destruction and recovery
Street furniture is generated from the road graph (lights every 38 m alternating sides, hydrants every 130 m,
signals at intersections) and from places (shelters at transit stops, benches in parks, dumpsters behind shops, bollards
at civic buildings), stored compactly in the `destruction` chunk and indexed in a 32 m grid. Damage comes from vehicle
impacts (kinetic energy; online, speed and vehicle are the server's own), power blasts and hurricane winds (per-prop,
per-hour deterministic rolls scaled by wind resistance). Consequences feed existing systems: witness visibility halves
at night where the lights are out, broken hydrants make the area wet for power/element interactions, and dead signals
cut the capacity of every road meeting at that intersection (BPR travel time). A property reaching `Destroyed` is a
structural collapse: EMS call sized by the people inside, leases ended with full deposits, debris damage, businesses
forced shut. Recovery: public-works crews (10 × Public Works service level) repair by priority and pay contractors from
the treasury; collapsed buildings not owned by players are rebuilt with a reconstruction grant after 60 days.

### 10.5 Government, elections, social media and radio
* **Budget.** Monthly: households' local taxes flow External → Treasury; each department is paid its share
  Treasury → External. Funding relative to need (per resident) becomes a service level that sets the number of police,
  fire and EMS units on duty (`EmergencyUnit.OffDuty`), the wanted-system response multiplier, flood-risk drift, park
  amenity and police trust. All of it goes through `TransactionProcessor`.
* **Ordinances** (`ordinances.json`) carry typed effects (tax rates, permit fee %, rent-increase cap, curfew, response
  multiplier, registration). Continuous effects are recomputed from the configured baseline on every enactment, repeal
  and load, so they are never written into the server config; one-time effects (foot traffic) change persisted
  district state. Unknown effect keys fail content validation.
* **Council and elections.** NPC officeholders vote by slate stance and district opinion (logistic, deterministic);
  players holding office vote explicitly. Elections run on `ElectionIntervalDays`: every adult NPC is a potential voter
  (turnout from age, conscientiousness and weather; choice from issue agreement, recognition and incumbency); players
  file (fee to the treasury, a campaign ledger account opened in the same transaction), raise capped donations, buy
  recognition with ads, and vote once.
* **Ripple** posts are generated from history records (so NPC chatter is always about something that happened), capped
  at 1,500 retained posts; player posts are rate-limited, mute-aware over the network, and earn NPC engagement from
  followers and public reputation.
* **Radio** runs on a real-time timeline (world clock ÷ time scale) so songs last their real length while game time
  runs ~30× faster. Each station's hour is built deterministically from the world when first requested: bulletins from
  history, weather, ads weighted by businesses' real ad spend, host lines with world tokens, emergency cut-ins.

## 11. World streaming (GDD §77, §113)

**City layouts.** `layout_vertical_slice.json` (3 districts) is the default. `layout_port_arden.json` is the full
19-district metro: `Tools/World/generate_metro.py` writes it deterministically, embedding the slice verbatim (every Story
Mode place keeps its name and position) and laying out the other sixteen bible districts with arterial grids, landmarks,
services, template businesses, parcel blocks and a connected freeway/avenue network (~7,500 places, ~50,000 residents,
302 businesses). CI fails if the file is out of date with its generator. Hosts choose with `--layout` (server, world
host) or `GameBootstrap.LayoutFile` (Unity); the editor menu can build a greybox scene of either.

* The city is partitioned into 256 m square cells, each an additive scene (`Cell_{x}_{z}`) → Addressables groups.
  `WorldStreamer` loads cells within 600 m (plus velocity look-ahead), unloads beyond 800 m (hysteresis), capped
  concurrent async operations.
* Each cell has HLOD proxies merged offline; beyond the load radius a city-wide HLOD layer renders skyline and
  terrain. Interiors are separate additive scenes loaded on approach to an entrance.
* Physics and NavMesh are per cell; the NavMesh is baked per cell and linked with NavMesh links at borders.
* The simulation never depends on what is loaded: NPCs, businesses and properties exist whether or not their cell is.

## 12. Asset pipeline, Blender and MCP (GDD §79–80, §156)

Full detail: [`ASSET_PIPELINE.md`](ASSET_PIPELINE.md). Summary:
* Blender 4.2 LTS; metres; Z-up in Blender → FBX with `-Z forward, Y up, FBX_SCALE_UNITS, bake space transform`.
* Naming: `SM_`/`SK_`/`M_`/`T_`, LODs `_LOD0..n`, collision `UCX_<mesh>_NN`.
* `Tools/Blender/hg_pipeline`: validation (names, applied transforms, UVs, materials, size, origin, LOD chain,
  triangle budgets, hull vertex limits), automatic LOD generation (planar dissolve + collapse), convex hulls, export with
  JSON sidecar reports, procedural generators (buildings, street furniture).
* Unity side: `ModelImportRules` enforces import settings, converts `UCX_` meshes to convex colliders and groups
  `_LODn` meshes into `LODGroup`s.
* **MCP integration points:** `hg_pipeline.mcp_commands` exposes JSON-in/JSON-out commands (`list_catalog`,
  `build_asset`, `validate_selection`, `generate_lods`, `generate_collision`, `export_selection`, `conventions_info`).
  A Blender MCP server calls `mcp_commands.run(...)`; the same commands run from the CLI and CI. Agents never
  hand-export: validation gates every export.

## 13. Security (GDD §88, §145)

* Server authority for all critical state (§8.3). Clients send intents, not results.
* Economy: double-entry invariant, journaled transactions, idempotency keys, audited admin grants, clamped config.
* Saves: `TypeNameHandling.None`; schema-version gate; invariant check on load and save.
* Content/mod data is data only (no scripting) until a sandboxed modding runtime is designed.
* Developer tools compile only in editor/development builds (`#if UNITY_EDITOR || DEVELOPMENT_BUILD`); server admin
  commands require `ServerPermission.WorldAdmin` and are logged.
* Accounts: PBKDF2 password hashes with lockout on the master server; game servers accept short-lived HMAC join tickets
  signed with a per-server derived key (constant-time comparison) and never see passwords.
* Every network request resolves positions from the server's own state (player position, NPC schedule positions),
  bounds every argument, and fails closed; handler exceptions are logged and answered with a generic error.
* The Phase 25 audit and its findings are in [`SECURITY_AUDIT.md`](SECURITY_AUDIT.md).

## 14. Testing strategy (GDD §143–144)

| Level | Tooling | Where |
|---|---|---|
| Core unit/system tests | NUnit 3 (classic asserts, Unity-compatible) | `Game/Assets/_Project/Tests/EditMode`, run by Unity Test Runner **and** `dotnet test` |
| Determinism tests | same | offline-vs-live equivalence, catch-up equivalence, generator determinism |
| Failure tests | same | journal failure, torn/garbage journal entries, crash after purchase, power cut between chunks and manifest, damaged snapshot fallback, corrupt manifest, failed save keeps its dirty set, throwing request handler, stale ownership, broken-ledger save refusal |
| Unity script compile check | `Headless/UnityCompileCheck` | CI, no Unity license needed |
| Asset pipeline | `unittest` + `bpy` | `Tools/Blender/tests` |
| Play mode / integration | Unity Test Runner PlayMode (Phase 1+) | GameCI when a license secret exists |
| Performance | `herogame-world bench` | CI artifact every run |

## 15. Deployment & build (GDD §117)

* **CI (`.github/workflows/ci.yml`):** .NET build + tests + Unity compile check (editor & player configs) + world
  smoke test + benchmark; Blender pipeline tests + kit export artifact; Unity `.meta` integrity; JSON validation.
* **Unity CI (`unity.yml`):** GameCI test runner and Windows client / Linux dedicated-server builds, enabled when
  Unity license secrets are configured.
* **Server distribution (Phase 10):** Linux server build + `herogame-world` tooling in a container image; community
  hosts run the same binaries.
* Branching: short-lived feature branches → PR → CI green → merge to `main`. Git LFS for binary art.

## 16. Performance targets (GDD §114, §142)

| Area | Target | Current measurement (container CPU, 4 cores, Release; `herogame-world bench`) |
|---|---|---|
| Client frame rate | 60 fps @1440p High on RTX 3070-class / 30–60 fps consoles | not yet measurable (greybox) |
| Server tick | 30 Hz with 128 players | **30 Hz held with 128 bot clients** (`herogame-server loadtest`, TLS on): main-thread p50 0.2 ms, p99 21 ms of a 33 ms budget; snapshots p50 6 ms for all 128; 12 KiB/s per client. Bots, sockets and server share one 4-core machine, so this overstates server cost |
| Whole daily world step (NPCs, businesses, civic incl. an election campaign, disasters, finance, courts, EMS, destruction) | ≤ 250 ms per game day at 50,000 NPCs | **measured on the full metro: 200–264 ms/day at 49,792 NPCs (4.0–5.3 µs per NPC-day)** across runs; 45 ms/day at 9,302 NPCs. At the target, with no headroom ⚠ |
| Population director | ≤ 2 ms per evaluation per server tick budget | **1.5–1.7 ms** at 50k NPCs / 7,530 places; 2.6–3.2 ms on the dense 9.3k stress layout (more candidates near the observers) ⚠ |
| Offline catch-up | ≤ 15 s for 60 days @50k NPCs | **12.9 s** for 60 days on the metro, including a 2.6 s load and 3.5 s full save |
| Incremental save | ≤ 16 ms hitch on the main thread | routine autosave: 12–21 ms blocked at 9.3k NPCs, **28–38 ms at 50k** ⚠. Save right after a daily step: **140–300 ms at 50k** (was ~860 ms), 83–113 ms at 9.3k (was ~270 ms): NPC and household snapshots are copied, not serialized, on the simulation thread; building layouts live in 16 copy-on-write shards rewritten only when a building changes ⚠ |
| Load + journal replay + invariant check | — | 1.5 s at 9.3k NPCs; 2.6–3.0 s at 50k |
| Server memory | — | 230–330 MiB managed for the 50k-NPC metro |
| Streaming hitch | none > 50 ms | Phase 2 |
| Client memory | ≤ 10 GB RAM / 8 GB VRAM (High) | Phase 25 |

⚠ items are tracked as technical debt in [`STATUS.md`](STATUS.md) and the dev report.

## 17. Development process (GDD §116, §148, §154, §187)

Every change: inspect → plan → implement modularly → compile → test → document → integrate. Status labels in
`STATUS.md` are only raised with evidence (a passing test, a measurement, a playable scene). Placeholders are
allowed only when listed in [`ASSET_TRACKER.md`](ASSET_TRACKER.md).
