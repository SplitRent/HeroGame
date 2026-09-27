# Development Report 002 — Systems pass (Phases 3–28)

**Scope:** everything after Report 001: the gameplay systems of GDD Phases 3–20 and the hardening phases 23–28, built on
the Phase 0 foundation. Commits `cd3c697` … this report on branch `claude/quirky-bell-ediz4x`.

**Environment constraints (unchanged, stated plainly):** no Unity editor or GPU was available. All engine-agnostic
code (simulation, persistence, networking, services) was **executed and tested** with .NET 8. Unity scripts were
**compile-checked only** (editor and player configurations, warnings as errors) and have **never run inside Unity**, so
every Unity-side row in [`STATUS.md`](../STATUS.md) stays **IN DEVELOPMENT** and no vertical-slice milestone in
[`VERTICAL_SLICE_PLAN.md`](../VERTICAL_SLICE_PLAN.md) is marked accepted: each acceptance criterion needs a play
session. That first Unity run is the most important next step (§6).

## 1. What was built

| Phase | Delivered (core = tested headlessly; Unity = compile-checked) |
|---|---|
| Perf debt | incremental NPC location index, spatial place grid, population shards saved in parallel |
| 3 | NPC memory-driven conversation, contextual barks, relationships, phone data layer (messages, contacts, statement, news, map search), interiors with real occupants |
| 4 | vehicle catalog and mods, ownership/fuel/damage/registration, road graph with A* and live congestion, statistical traffic; Unity driving, entry/exit, traffic presenter |
| 5 | wall/room/opening/furniture layouts with an architectural validator, priced construction sessions committed atomically, change of use (warehouse → nightclub), rental units, rent and eviction, property tax and tax sales; Unity build mode |
| 6 | journal-safe records (accounts/loans/policies/businesses ride in their transaction), underwriting (credit score, LTV, DTI), mortgages, loans, savings, insurance (property, vehicle, business interruption, health), hurricane damage and repairs, full player business management with real NPC staff |
| 7 | shoplifting, pickpocketing, burglary, store robbery, vehicle theft, assault, vandalism; loot and fences; charging thresholds, bail, counsel, pleas, verdicts, sentencing, custody, probation, fines and warrants |
| 8 | emergency dispatch by road ETA, police arrest on scene, fire growth/spread/suppression, EMS, hospital billing and medical debt, background calls at per-capita rates |
| 9–10 | binary TCP protocol, authoritative game server with movement validation, rate limits and idempotent requests, HMAC join tickets, master server (accounts, server directory, tickets), dedicated server host, moderation over the network; Unity online session |
| 11–14 | power execution: effect planning from composition, validity rules, environment requirements, effects on NPCs/players/vehicles/buildings, collateral, shields, healing, teleport, witnesses, secret-identity clues, notoriety, energy-signature evidence; Unity power controller |
| 15–17 | data-driven story framework (missions, objectives, branching dialogue, scripted effects/conditions, cutscene requests) with strict validation; Part One "Magnolia Street" (7 missions) and a four-year time jump that really simulates the years |
| 19–20 | municipal budget driving real service levels, ordinances with typed effects, council votes, district opinion moved by events, elections with an NPC electorate and player candidates, the registration ordinance, Ripple social platform, city calendar, local disasters (flash flood, chemical incident, blackout, heat wave) |
| 23–24 | radio (4 original stations on a real-time timeline: history-based news, weather, ads for real businesses, host breaks, emergency cut-ins); phone panel with messages, map, news, radio, Ripple, bank, loans, insurance and businesses; radio presenter |
| 25 | security audit of the whole remote surface ([`SECURITY_AUDIT.md`](../SECURITY_AUDIT.md)): 3 findings fixed with tests, 2 open (TLS, session revocation) |
| 26 | failure testing: power cut between chunk writes and manifest, failed save keeps its dirty set, damaged-snapshot fallback to the previous generation with journal replay, corrupt manifest, garbage journal entries, throwing request handler |
| 27–28 | TDD brought up to date (persistence fallback, disasters, government/social/radio, security, testing, performance), STATUS board, asset tracker, networking catalogue, this report |

Phases 18 and 21–22 were not part of this pass, and world expansion beyond the slice waits for the slice to be
accepted in Unity (VS-8).

## 2. Test results
* **187/187** NUnit tests (`dotnet test Headless/HeroGame.sln -c Release`, the same sources Unity's Test Runner runs)
  and **4/4** service tests. Per area: civic & social 18, finance & business 15, networking 14, crime & justice 11,
  economy 11, emergency 11, population 11, foundation 10, powers 9 + 8, vehicles 9, world simulation 9,
  building & rental 8, crime & identity 8, social & phone 8, persistence 7, story 7, radio 6, failure 5, server 2.
* Unity compile check: editor ✔, player ✔ (0 warnings). `.meta` integrity ✔ (278 assets). One-MonoBehaviour-per-file
  guard ✔.
* CI (GitHub Actions) green on every pushed phase through runs 1–11; later runs are checked after each push.
* Highlights: elections are deterministic and turnout lands in a plausible band; a gutted police budget visibly takes
  cars off duty and they stop taking calls; ordinance tax rates survive a restart without being written into the
  server config; a chemical leak closes exactly its district's businesses; paying for advertising buys radio airtime;
  a truncated transactional chunk is recovered from the previous snapshot with every grant since replayed.

## 3. Performance (container CPU, Release, stress layout: 9,302 NPCs, 4,449 places)
| Measurement | Report 001 | Now |
|---|---|---|
| Whole daily world step (incl. civic with an election campaign, disasters, finance, courts, EMS, Ripple) | 3.4–4.4 µs per NPC-day (NPCs only) | **20.8 ms/day, 2.23 µs per NPC-day** |
| Population director (8 observers) | 6.0–6.5 ms ⚠ | **1.6 ms** |
| Full save | 1.5–1.8 s (18 MB) | 1.9 s (24.7 MB, 17 chunks) |
| Incremental save after one day | 0.6–1.0 s ⚠ | 1.1 s ⚠ (14 of 17 chunks dirty daily) |
| Load + journal replay + invariant | ~1.1 s | 1.4 s |
| Managed memory | 34 MiB (10k NPCs) | 117 MiB (9.3k NPCs, all systems) |

Extrapolated to the 50,000-NPC target the daily step is ~112 ms (target ≤ 250 ms).

## 4. Problems found and fixed along the way (selection)
1. Every MonoBehaviour shared files with others — Unity would have saved them into scenes as missing scripts. Split
   into one file each, and CI now enforces it.
2. Radio segments were first scheduled in game seconds: at 30× time a 3-minute song would have lasted 6 real
   seconds. Radio now runs on the clock divided by its time scale.
3. `crime.pickpocket` over the network trusted the client about where the victim was (audit finding #1).
4. Journal compaction after every save made the previous snapshot unrecoverable; compaction now stops at the
   previous generation.
5. A council district with no educated 35+ residents got no council seat; such wards are now represented by someone
   from elsewhere.
6. Staff turnover was unrealistically high and was recalibrated to an annual hazard driven by relative pay; a door on a
   wall inside a larger room was wrongly unreachable in the building validator.

## 5. Known gaps and technical debt
* **Never run in Unity** (all presentation, input, UI). IMGUI placeholders everywhere a UI Toolkit screen is planned.
* No music, VO or SFX: radio and story are subtitle-only (ASSET_TRACKER "MISSING").
* Incremental saves still take ~1 s and run on the calling thread; a binary format and background serialization are
  needed before the 16 ms hitch target (TDD §16 ⚠).
* No transport encryption; master sessions not revocable (audit #4, #5). No load test of the game server yet.
* Youth curfew has no per-NPC enforcement; campaign funds left after an election stay in the campaign account.
* Replication of player-made world changes (built walls, fires) to other clients is not implemented (NETWORKING.md).
* Story Mode Part Two acts II–IV are not written.

## 6. Recommended next steps
1. Open the project in Unity 6, run the setup and greybox builder, fix whatever the first run surfaces, and work
   through VS-1 acceptance. Only then raise Unity rows in STATUS.
2. TLS for the master (reverse proxy) and the game protocol; token revocation.
3. Background/binary incremental saves; a scripted 128-client load test of the game server.
4. Commission original music for the 25 listed tracks; UI Toolkit phone and HUD.
