# Development Report 003: World, replication and polish pass

**Scope.** This report covers everything after Report 002, which ended at commit `444c2bc`. Commits `c7b422a` through
`81e91ea` are on branch `claude/quirky-bell-ediz4x`. The work was:

* the Unity health check;
* Phases 27 and 28 hardening;
* Phase 25 optimisation;
* Phase 20 destruction;
* Phase 23 audio;
* debug tools;
* Phase 18 and Story Mode Part Two;
* Phases 21–22 (the full metro);
* shared-world replication;
* Phase 24 UI/UX.

**Environment constraints.** These are unchanged from the previous reports.

* No Unity editor or GPU was available here.
* Engine-free code was executed and tested with .NET 8. This covers the simulation, persistence, networking and services.
* Unity scripts were only compile-checked, in both the editor and player configurations, with warnings treated as errors.
* The project owner has opened the project in Unity, but no Health Check report or Console output has come back yet.
  Every Unity row in [`STATUS.md`](../STATUS.md) therefore stays **IN DEVELOPMENT**.

## 1. What was built

| Area | Delivered (core = tested headlessly; Unity = compile-checked) |
|---|---|
| Unity health check | `HeroGame ▸ Health Check (run me first)`. It checks the Unity version, HDRP, input handling, colour space, content and story validation, a simulation and save round-trip smoke test, and scenes and build settings. It writes `Logs/HeroGame_HealthCheck.txt`, copies the report to the clipboard and says what to do next. |
| 27: failure tests | <ul><li>A disconnect mid-purchase followed by a retry charges once.</li><li>Leaving during a chase counts as evading, and manhunts survive restarts.</li><li>Shutdown notices reach every player.</li><li>Loading twice never replays money twice.</li><li>Fencing the same loot twice pays once.</li><li>Every power combination is safe.</li><li>Positions from power requests are bounded.</li></ul> |
| 28: security | <ul><li>TLS 1.2 on the game protocol with SHA-256 certificate pinning. The fingerprint is published through the master server and carried in join tickets.</li><li>The dedicated server uses a self-signed certificate by default.</li><li>HTTPS option for the master server, with a warning when it runs plaintext on a network.</li><li>Sign out, sign out everywhere, and password change revoke sessions.</li></ul> |
| 25: optimisation | <ul><li>`herogame-server loadtest` runs real bot clients over loopback, optionally over TLS.</li><li>Each connection has a writer thread with a bounded queue.</li><li>Snapshots use interest management: the nearest 32 players.</li><li>Saves are split into a snapshot step and a commit step, and `SaveInBackground` runs the commit off the simulation thread.</li></ul> |
| 20: destruction | <ul><li>Street furniture is generated from roads and places: lights, signals, hydrants, benches, shelters, dumpsters and bollards.</li><li>Damage states have consequences: dark streets, flooded streets, slow intersections.</li><li>Server-validated vehicle impacts, power blasts and hurricane winds break props.</li><li>Structural collapse ends leases, closes businesses and calls EMS.</li><li>Public-works repair is paid from the treasury, and the city rebuilds after 60 days.</li></ul> |
| 23: audio | <ul><li>Deterministic procedural placeholder synthesis: music loops per radio track, sirens, engines, weather, five ambience beds, UI sounds, power sounds and voice babble.</li><li>The soundscape mixer is driven by district, time of day, weather, crowds and real emergency units.</li><li>Unity side: audio director and vehicle audio.</li></ul> |
| Debug tools | `AdminCommands` is shared by the dev console, the server console and `admin.cmd`. `admin.cmd` requires WorldAdmin and is audited. The world inspector shows events, emergencies, manhunts, elections, broken props and network state. |
| 18 and Story Part Two | <ul><li>Eleven missions across Acts II–IV and the finale, with three endings (saved, sold, split).</li><li>Missions drive real systems: fire dispatch, a council vote, an election, a storm, a chemical incident and property transfers.</li><li>After the credits the sandbox continues.</li><li>Also added: a youth curfew in NPC schedules, weekend late nights, and campaign funds that carry over between runs.</li></ul> |
| 21–22: full metro | <ul><li>`Tools/World/generate_metro.py` builds `layout_port_arden.json`: all 19 bible districts, with the vertical slice embedded verbatim.</li><li>Districts get landmarks, services, 302 template businesses, about 7,500 places, about 50,000 residents, and one connected freeway and avenue network.</li><li>Saves record their layout, and hosts reopen a world on it.</li><li>`--layout` works on the server and world host; `GameBootstrap.LayoutFile` selects it in Unity.</li><li>New editor item: a metro greybox builder.</li></ul> |
| Save stall | <ul><li>NPC and household records are snapshot-copied rather than serialized on the simulation thread.</li><li>Building layouts moved into 16 copy-on-write shards that are rewritten only when a building changes.</li><li>A failed synchronous save now retries its chunks.</li><li>Result: the blocking time after a daily step dropped about 3× (§3).</li></ul> |
| Replication | <ul><li>`WorldDelta` carries broken props, fires, player ownership, sale listings, damage and rebuilt-layout versions.</li><li>New players get the full state on join; after that the server sends diffs every 500 ms.</li><li>`property.layout` and `LayoutData` fetch rebuilt buildings.</li><li>The client keeps a `ReplicatedWorld` mirror. Unity presenters use it while online.</li></ul> |
| 24: UI/UX | <ul><li>Validated, persisted settings covering audio, controls, display and accessibility.</li><li>A notification queue that folds duplicates, prioritises and stays bounded.</li><li>Property portfolio and inventory view-models.</li><li>Unity side: an Esc settings menu that applies changes live, toasts with shape and colour cues, and phone Properties and Inventory tabs.</li></ul> |

## 2. Test results
* NUnit: **231/231** tests pass (`dotnet test Headless/HeroGame.sln -c Release`, the same sources Unity's Test Runner
  uses). Service tests: **6/6** pass.
* Report 002 ended at 187 NUnit tests. New suites since then:
  * `FailureTests` additions
  * `DestructionTests`
  * `AudioTests`
  * `AdminCommandTests`
  * `MetroLayoutTests`
  * `SaveSnapshotTests`
  * `PlayerUiTests`
  * new `NetworkingTests`
  * new `StoryTests`
* CI:
  * Runs build, tests, both Unity compile checks and a world smoke test.
  * Runs a dedicated-server and master smoke test over TLS.
  * Runs a 64-client TLS load test.
  * Runs benchmarks on the stress layout and the metro layout.
  * Checks Blender pipeline tests, `.meta` integrity, data JSON validity and metro-generator freshness.
  * Runs 1–24 were green when checked. Run 25 was queued at the time of checking.

## 3. Measurements (4-core container, Release)
| Measure | Result | Target |
|---|---|---|
| Server tick, 128 bot clients over TLS | 30 Hz held; p99 18–21 ms | 33 ms budget |
| Server tick, 64 clients over TLS | p99 11.6 ms, including world replication | same |
| Bandwidth per client | about 12 KiB/s | — |
| Daily world step, 49,792 NPCs (metro) | 200–264 ms per game day | ≤ 250 ms ⚠ |
| Population director, 50k NPCs | 1.5–1.7 ms per evaluation | ≤ 2 ms |
| Population director, dense 9.3k stress layout | 2.6–3.2 ms per evaluation | ≤ 2 ms ⚠ |
| 60-day offline catch-up, 50k NPCs | 12.9 s, including load and save | ≤ 15 s |
| Save after a daily step, 50k NPCs | 140–300 ms blocked (was ~860 ms) | 16 ms ⚠ |
| Save after a daily step, 9.3k NPCs | 83–113 ms blocked (was ~270 ms) | 16 ms ⚠ |
| Routine autosave | 12–21 ms at 9.3k; 28–38 ms at 50k | 16 ms ⚠ |
| Load, 50k NPCs | 2.6–3.0 s | — |
| Managed memory, 50k NPCs | 230–330 MiB | — |

## 4. Honest gaps and technical debt
* **Unity runtime is still unverified.** This remains the most important next step. Run the Health Check, press Play
  in `VerticalSlice_Greybox`, and send back the report and any red Console errors.
* **Save stalls are still over budget.** The remaining blocking cost is serializing the properties, environment,
  transactional and household data after a daily step. The next steps are to detach those as well, or to time-slice
  them, and to adopt a binary format (tech debt #1).
* **The 50k daily step is at the target with no headroom.** Profile it by system next; business and population are
  the biggest parts.
* **The metro is data, not art.** It has street grids and parcel blocks but no terrain, water or coastline shapes,
  and landmark buildings are boxes.
* **Online clients still keep a local presentation world.** Only shared world changes are replicated. Banking and
  businesses go through requests but have no dedicated online screens yet.
* **The new UI is IMGUI placeholder.** It sits over tested view-models and waits for the UI Toolkit skin
  (ASSET_TRACKER).

## 5. Next steps
1. Get the first Unity run report, fix whatever the Health Check or Console shows, then play-test the vertical-slice
   acceptance list.
2. Detach the remaining post-daily-step chunks and profile the 50k daily step.
3. Build UI Toolkit skins for the phone, settings and toasts.
4. Build online bank and business views over requests.
5. Commission art for the metro: terrain, water, district kits and landmarks. The asset pipeline is ready.
