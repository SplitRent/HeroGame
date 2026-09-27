# Live Status

Labels (GDD §148): **PLANNED · PROTOTYPING · IN DEVELOPMENT · FUNCTIONAL · POLISHED · TESTED · OPTIMIZED · COMPLETE**

Rules: a label is raised only with evidence. "Evidence" column names the test, measurement or tool that justifies it.
*Code written but never executed inside Unity* is at most **IN DEVELOPMENT** — Unity has not been run on this project
yet (no editor in the development environment); Unity scripts are verified by compiling against Unity reference
assemblies only.

_Last updated: Phases 9–10 (networking, servers, accounts)._

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
| Population director (tiers, materialisation set) | TESTED | `Director_*`, `LocationIndex_AgreesWithDirectResolution`; expiry-heap + spatial-hash index |
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
| NPC barks & memory-driven dialogue | TESTED | `Barks_*`, `RepeatedFriendlyContact_BuildsARelationship`, `Threats_AndWitnessedCrimes_AreRemembered`, `Gossip_UsesRealServerHistory` |
| Phone core (messages, statement, news, map search) | TESTED | `Phone_*` |
| Vehicles (dealership, fuel, wear, damage, repair, mods, garages, impound) | TESTED | `VehicleTests` (9) |
| Road graph, routing, aggregate traffic | TESTED | `RoadNetwork_*`, `Traffic_PeaksAtRushHourAndFallsInStorms` |
| Building layouts (walls/rooms/openings/furniture) + validator | TESTED | `EveryGeneratedLayout_IsValid`, `Validator_CatchesUnbelievableArchitecture` |
| Construction (preview, pricing, atomic commit), change of use | TESTED | `WarehouseToNightclub_ConversionEndToEnd`, `Construction_IsAtomic_*`, `WorldBuild_*`, `BuildTools_*` |
| Rentals (units, deposit, rent, eviction) & property tax / tax sale | TESTED | `Renting_CollectsRentAndEvictsNonPayers`, `PropertyTax_ArrearsLeadToTaxSale` |
| Journal-safe records (accounts, loans, policies, businesses ride in the transaction) | TESTED | `Crash_AfterMortgageAndFounding_RecoversLoanBusinessAndAccount` |
| Underwriting (credit score, LTV, DTI, asset depletion), mortgages, early repayment | TESTED | `Underwriter_*`, `Mortgage_*`, `PersonalLoan_EarlyRepayment_*` |
| Savings accounts & interest | TESTED | `Savings_OpenOnce_TransferAndEarnMonthlyInterest` |
| Insurance (property, vehicle, business interruption, health), premiums, lapse, claims | TESTED | `PropertyInsurance_*`, `Premiums_*`, `HealthInsurance_*`, `BusinessInterruption_*` |
| Hurricane property damage + repairs | TESTED | `StormDamage_IsDeterministic_AndHitsFloodProneDistrictsHarder` |
| Player businesses (buy, found, manage, hire/fire NPCs, turnover, draw, sell) | TESTED | `BuyingABusiness_*`, `Management_*`, `UnderpaidStaff_*`, `FoundingANightclub_*` |
| Crime actions (shoplifting, pickpocketing, burglary, robbery, vehicle theft, assault, vandalism) | TESTED | `Shoplifting_*`, `Burglary_*`, `StoreRobbery_*`, `VehicleTheft_*`, `Masks_ReduceIdentification_*` |
| Loot, fences, chop shops (items.json) | TESTED | `StoreRobbery_TakesFromTheTill_AndTheFenceBuysLoot`, `VehicleTheft_*` |
| Legal system (charging threshold, bail, counsel, plea, verdicts, sentencing, custody, probation, fines, warrants) | TESTED | `Sentencing_*`, `Robbery_Arrest_Bail_Hearing_Sentence_EndToEnd`, `GuiltyPlea_*`, `UnpaidFines_*`, `Arrest_WithoutEvidence_*` |
| Justice persistence (incidents, evidence, cases) | TESTED | `EvidenceAndCases_SurviveSaveAndLoad` |
| Emergency dispatch (road-ETA unit selection, priority queue, trips, scenes, returns) | TESTED | `MedicalCall_*`, `SurgeOfCalls_*`, `Units_AreCrewedFromServiceFleets` |
| Police response & arrest on scene | TESTED | `Police_ArrestASuspectStillAtTheScene_ButNotOneWhoLeft` |
| Fire growth, suppression, spread, building loss | TESTED | `Fire_IsFoughtAndPutOut_Deterministically`, `Fire_WithNoEnginesAvailable_BurnsTheBuildingDown` |
| EMS transport, hospital stay, billing via health cover, medical debt, permadeath rule | TESTED | `DownedPlayer_*`, `UninsuredBrokePatient_*`, `Permadeath_*` |
| Background city calls at realistic rates | TESTED | `BackgroundCalls_ComeInAtRealisticRates_AndAreAnswered` |

## Persistence
| System | Status | Evidence / notes |
|---|---|---|
| Chunked snapshots + manifest | TESTED | `World_SavesAndLoadsFaithfully`, `IncrementalSave_*` |
| Write-ahead journal + recovery | TESTED | `Crash_AfterPurchase_*`, `TornJournalTail_IsDiscarded` |
| Integrity gate (refuse broken ledger) | TESTED | `Save_RefusesToPersistBrokenLedger` |
| Story save slots | TESTED | `StorySlots_RotateAutosaves` |
| Account profiles (local) | FUNCTIONAL | used by front end |
| Binary save format | PLANNED | tech debt #1 |
| PostgreSQL backend | PLANNED | file-backed JSON stores in use; see NETWORKING.md |

## Networking & services (see NETWORKING.md)
| System | Status | Evidence / notes |
|---|---|---|
| Wire protocol (framing, bounds, hostile input) | TESTED | `Wire_RoundTripsMessages_AndRejectsHostileFrames` |
| Join tickets (per-server keys, expiry, forgery) | TESTED | `Tickets_AreValidOnlyForTheirServer_UntilExpiry_AndCannotBeForged` |
| Authoritative TCP server (auth, sessions, snapshots, chat) | TESTED | `TwoPlayers_*`, `BadTickets_*`, `SilentConnections_*` |
| Anti-cheat: movement validation, proximity, rate limits, malformed frames | TESTED | `Movement_*`, `CrimeRequests_RequireBeingThere_*`, `Floods_AndMalformedFrames_*` |
| Request router onto core services, idempotent retries | TESTED | `Requests_RunAsTheConnectionsCharacter_*`, `BuildOps_SurviveTheWireEncoding` |
| Moderation over the network (kick/ban/mute/grant, audit log, persistence) | TESTED | `Moderation_OnlyPermittedAccountsCanKick_*` |
| Accounts (PBKDF2, lockout, sessions) | TESTED | `Accounts_ValidateInput_HashPasswords_AndLockAfterRepeatedFailures` |
| Server directory (registration, heartbeat, listing, tickets) | TESTED | `Servers_RegisterUnderAnAccount_*`, `Stores_PersistAtomically` |
| Master HTTP API + full-stack join | TESTED | `FullStack_LoginOverHttp_TicketFromMaster_JoinTheGameServer`; CI smoke test |
| Dedicated server host (`herogame-server`) | FUNCTIONAL | CI smoke test (create, status, save, stop) |
| Unity online session (login, join, movement, remote players) | IN DEVELOPMENT | compiles; not run in Unity |
| Replication of player-made world changes to other clients | PLANNED | see NETWORKING.md limitations |

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
| NPC conversation, subtitles, interiors with real occupants | IN DEVELOPMENT | compiles |
| Vehicle controller (WheelCollider), entry/exit, traffic presenter | IN DEVELOPMENT | compiles |
| Build mode (overhead editor, live cost/validation, layout renderer) | IN DEVELOPMENT | compiles; greybox builder adds a build zone to every property |
| Business office panel, bank/insurance phone apps | IN DEVELOPMENT | compiles; IMGUI placeholders |
| Crime interactables (shelves, register, break-in, fence, chop shop), police desk, custody | IN DEVELOPMENT | compiles; greybox builder places them |
| Emergency unit & fire presentation, player vitals (fall damage, downed → hospital) | IN DEVELOPMENT | compiles |
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
