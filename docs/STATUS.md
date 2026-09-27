# Live Status

Labels (GDD §148): **PLANNED · PROTOTYPING · IN DEVELOPMENT · FUNCTIONAL · POLISHED · TESTED · OPTIMIZED · COMPLETE**

Rules: a label is raised only with evidence. "Evidence" column names the test, measurement or tool that justifies it.
*Code written but never executed inside Unity* is at most **IN DEVELOPMENT** — Unity has not been run on this project
yet (no editor in the development environment); Unity scripts are verified by compiling against Unity reference
assemblies only.

_Last updated: Phase 0 (foundation)._

## Foundation
| System | Status | Evidence / notes |
|---|---|---|
| Repository, folders, asmdefs, standards | FUNCTIONAL | this repo; `CODING_STANDARDS.md` |
| CI (.NET, compile check, Blender, meta integrity) | FUNCTIONAL | `.github/workflows/ci.yml` (first run happens on push) |
| Unity CI (GameCI tests/builds) | IN DEVELOPMENT | `unity.yml`; needs Unity license secrets |
| Technical Design / Game Bible / Slice plan | FUNCTIONAL | `docs/` |
| Stable IDs, deterministic RNG, clock, event bus | TESTED | `FoundationTests` |
| Server configuration + integrity clamps | TESTED | `ServerConfig_*` tests |
| Data-driven content + validation | TESTED | `Content_PassesValidation`; 9 data files |

## Simulation core (engine-agnostic)
| System | Status | Evidence / notes |
|---|---|---|
| Double-entry ledger, invariant | TESTED | `EconomyTests` |
| Atomic world transactions, idempotency | TESTED | `Purchase_*`, `IdempotencyKey_*` |
| Loans / mortgages | TESTED | `Loan_*` (amortisation, payoff, default & repossession path) |
| Taxes | FUNCTIONAL | used by purchases and businesses; no dedicated tests yet |
| Macro economy (cycle, inflation, unemployment) | FUNCTIONAL | runs daily; not tuned |
| Property registry, purchase, valuation, wear | TESTED | `Crash_AfterPurchase_*`, `GeneratedWorld_*` |
| Business aggregate simulation | TESTED | `Storms_*`, `Understaffed_*`, `BrokeBusinesses_*`; balance needs tuning |
| NPC generation (families, jobs, homes) | TESTED | `Generator_*`, `Households_HaveCoherentFamilies` |
| NPC schedules (stateless) | TESTED | `Schedule_*` |
| NPC life sim + offline catch-up | TESTED | `CatchUp_InOneStepEqualsManySmallSteps`, `OfflineCatchUp_MatchesLiveSimulation` |
| Social sim (dating, marriage, divorce, births) | FUNCTIONAL | runs in 30-day tests; no dedicated assertions |
| NPC memory of players | FUNCTIONAL | `Memory_IsBoundedAndKeepsSignificantPeople`; not yet used by dialogue |
| Population director (tiers, materialisation set) | TESTED | `Director_*`; ⚠ 6 ms/eval at 4.4k places — needs spatial index |
| Weather + tropical systems | TESTED | `Weather_IsDeterministicAndHurricanesOnlyInSeason` |
| Crime: witnesses, evidence, wanted | TESTED | `CrimeAndIdentityTests` |
| Secret identity discovery | TESTED | `SecretIdentity_*` |
| Reputation (multi-axis) | TESTED | `Reputation_*` |
| Power framework (composition, complexity, rarity) | TESTED | `Rarity_EmergesFromComplexity`, `Signatures_*` |
| Anomaly events & exposure | TESTED | `Anomalies_AreRareAndDeterministic`, `Exposure_*` |
| Multiple powers rarity | TESTED | `MultiplePowers_AreExtraordinarilyRare` |
| Power discovery & progression | TESTED | `Discovery_ProgressesFromLatentThroughPractice` |
| Power interaction rules | TESTED | `Interactions_AreDataDriven` |
| Server history & news | FUNCTIONAL | `News_IsBuiltFromRealHistory` |
| Server browser filtering | TESTED | `Browser_FiltersAndSorts` |
| Moderation roles/permissions | TESTED | `Moderation_EnforcesRanksAndLogsActions` |

## Persistence
| System | Status | Evidence / notes |
|---|---|---|
| Chunked snapshots + manifest | TESTED | `World_SavesAndLoadsFaithfully`, `IncrementalSave_*` |
| Write-ahead journal + recovery | TESTED | `Crash_AfterPurchase_*`, `TornJournalTail_IsDiscarded` |
| Integrity gate (refuse broken ledger) | TESTED | `Save_RefusesToPersistBrokenLedger` |
| Story save slots | TESTED | `StorySlots_RotateAutosaves` |
| Account profiles (local) | FUNCTIONAL | used by front end |
| Binary save format | PLANNED | tech debt #1 |
| PostgreSQL backend | PLANNED | Phase 9–10 |

## Headless tools
| System | Status | Evidence / notes |
|---|---|---|
| `herogame-world` host (new/run/resume/inspect/bench) | FUNCTIONAL | run in CI; see dev report for output |
| Server/world/NPC inspectors (text) | FUNCTIONAL | `inspect` subcommands |

## Unity runtime (compile-checked, not yet run in Unity)
| System | Status | Evidence / notes |
|---|---|---|
| Bootstrap, session, autosave | IN DEVELOPMENT | compiles vs Unity refs |
| Third-person motor & camera | IN DEVELOPMENT | compiles; needs play test |
| Input System adapter | IN DEVELOPMENT | not compile-checked (package not available offline) |
| Interaction system (doors, buy property, counters) | IN DEVELOPMENT | compiles |
| NPC presentation (pooled avatars) | IN DEVELOPMENT | compiles |
| Day/night, weather presentation | IN DEVELOPMENT | compiles |
| World streaming (cells) | IN DEVELOPMENT | compiles; no cell scenes yet |
| Main menu, server browser UI, character creator | IN DEVELOPMENT | compiles |
| Dev console, world/NPC inspector overlay | IN DEVELOPMENT | dev builds only |
| Greybox world builder (editor) | IN DEVELOPMENT | compiles; generates scene from layout data |
| Model import rules (UCX, LODs, textures) | IN DEVELOPMENT | compiles |

## Asset pipeline
| System | Status | Evidence / notes |
|---|---|---|
| Naming conventions | TESTED | `test_conventions.py` |
| Validation (transforms, UVs, materials, budgets, LODs, hulls) | TESTED | `test_every_catalog_asset_validates`, `test_validation_catches_*` |
| LOD + convex collision generation | TESTED | `test_lods_decrease_and_collision_exists` |
| FBX export (Unity contract) | TESTED | `test_fbx_round_trip_preserves_metric_dimensions` |
| Procedural building/prop kit (15 assets) | FUNCTIONAL | blockout quality — see `ASSET_TRACKER.md` |
| MCP command surface | FUNCTIONAL | `mcp_commands.run`; exercised via CLI |

## Not started (PLANNED)
Vehicles & traffic · building/construction mode · interiors · police/fire/EMS AI · combat & weapons · networking &
dedicated server · master server & accounts service · elections & government · phone · social media · radio & audio ·
dialogue · cutscenes & Story Mode content · animation set · character creator 3D preview · destruction · Addressables
cell content · HLOD · full city.
