# Live Status

Labels (GDD §148): **PLANNED · PROTOTYPING · IN DEVELOPMENT · FUNCTIONAL · POLISHED · TESTED · OPTIMIZED · COMPLETE**

Rules: a label is raised only with evidence. "Evidence" column names the test, measurement or tool that justifies it.
*Code written but never executed inside Unity* is at most **IN DEVELOPMENT** — Unity has not been run on this project
yet (no editor in the development environment); Unity scripts are verified by compiling against Unity reference
assemblies only.

_Last updated: Phases 23–28 (radio, phone apps, security audit, failure testing, documentation). See `reports/DEV_REPORT_002_SYSTEMS.md`._

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
| Power effects from composition (strike, blast, movement, teleport, shield, heal, telekinesis, sense, disguise, time) | TESTED | `EffectPlanner_DerivesEffectsFromComposition` |
| Power execution: stage, cooldown, custody, range, server switches/caps, environment needs | TESTED | `Use_RespectsStage_*`, `EnvironmentRequirements_Apply` |
| Power consequences: damage, collateral, fire, EMS, crimes, energy signatures | TESTED | `Firebolt_*`, `Strike_OnAPerson_*` |
| Shields, healing, teleport, stamina/strain recovery | TESTED | `Shield_*`, `Teleport_*`, `StaminaAndStrain_*` |
| Public use → identity clues, notoriety, news; costumes protect | TESTED | `PublicUse_OutOfCostume_*` |
| Powers over the network (server-authoritative origin, teleport correction) | TESTED | `PowerUse_OverTheWire_*` |
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
| Municipal budget (household taxes in, department funding out) → units on duty, police response, flood defences, trust | TESTED | `MonthlyBudget_MovesRealMoney_AndCutsTakeUnitsOffDuty` |
| Ordinances with typed effects (tax rates, permit fees, rent stabilization, curfew, registration), enact/repeal, restored on load | TESTED | `Ordinances_ChangeRates_AndRepealRestoresThem`, `RentStabilization_CapsRaisesForSittingTenants`, `CivicAndSocialState_SurviveSaveAndLoad` |
| Council agenda & votes (NPC members lean by slate + district opinion; player members vote) | TESTED | `Council_VotesOnProposals_AndTheOutcomeTakesEffect`, `PlayerCouncilMember_VoteIsCounted` |
| District opinion on five issues, moved by real events | FUNCTIONAL | `NewCity_HasABudget_*`; event shocks exercised by simulation, not asserted per event |
| Elections (NPC electorate, turnout, polls, filing fee, campaign accounts, donations, ads, ballots, officeholder change) | TESTED | `Election_PlayerFilesCampaignsAndVotes_AndTheWinnerTakesOffice`, `Elections_AreDeterministic`, `PlayerElectionsDisabled_BlocksCandidacy` |
| Anomalous-abilities registration ordinance → unregistered public use is a crime | TESTED | `RegistrationOrdinance_MakesUnregisteredPublicUseACrime` |
| Ripple social platform (NPC posts from real history, player posts, likes, follows, feed ranking, trending, NPC engagement) | TESTED | `Ripple_NpcsPostAboutRealEvents`, `Ripple_PlayersPostLikeFollowAndReadAFeed` |
| City calendar (dated events shifting demand by place kind, announcements) | TESTED | `Calendar_EventsSpanTheirDates_AndShiftDemand` |
| Local disasters: chemical incident (district closure), flash flood (damage), blackout (outage hours), heat wave (EMS calls, demand) | TESTED | `ChemicalIncident_ClosesTheDistrictsBusinesses_AndIsCalledIn`, `FlashFlood_DamagesPropertyOnlyInItsDistrict`, `Blackout_AccruesOutageHoursForItsDistrict` |
| Radio (4 original stations; real-time running order; news from real history, weather, ads for real businesses weighted by ad spend, host breaks, emergency cut-ins) | TESTED | `EveryStation_FillsTheHourWithContiguousSegments`, `OnAir_IsDeterministic_AcrossWorlds`, `Songs_LastTheirRealLength_*`, `News_ComesFromRealHistory`, `Advertisers_GetAirtime`, `Emergencies_CutIntoEveryStation` |
| Youth curfew enforcement on individual minors | PLANNED | the ordinance exists and costs police trust in poorer districts; no per-NPC enforcement yet |

## Story Mode
| System | Status | Evidence / notes |
|---|---|---|
| Story framework (missions, objectives, branching, dialogue trees, effects/conditions, cutscene requests) | TESTED | `Dialogue_*`, `RafasFavor_RefusingSkipsTheDeliveryBranch` |
| Story content validation (ids, places, branches, scripted effects) | TESTED | `StoryContent_PassesValidation`, `Validator_CatchesWriterMistakes` |
| Cast as real NPCs in a 2026 world; teen restrictions | TESTED | `Begin_CastsRealPeople_InARealWorld_In2026` |
| Part One "Magnolia Street" (7 missions) + four-year time jump (simulated) + Part Two opener | TESTED | `PartOne_PlaysThrough_Isadora_AndTheFourYearJump` (scripted playthrough) |
| Story save/resume | TESTED | `StoryState_SurvivesSaveAndLoad_MidMission` |
| Part Two acts II–IV and finale | PLANNED | GDD §6; framework ready, content not written |
| Story presenter (objective HUD, dialogue box, letterbox cutscenes, interaction points) | IN DEVELOPMENT | compiles; IMGUI placeholder; not run in Unity |
| Voice acting, animation, Timeline cutscenes | PLANNED | ASSET_TRACKER |

## Persistence
| System | Status | Evidence / notes |
|---|---|---|
| Chunked snapshots + manifest | TESTED | `World_SavesAndLoadsFaithfully`, `IncrementalSave_*` |
| Write-ahead journal + recovery | TESTED | `Crash_AfterPurchase_*`, `TornJournalTail_IsDiscarded` |
| Integrity gate (refuse broken ledger) | TESTED | `Save_RefusesToPersistBrokenLedger` |
| Power cut between chunk writes and manifest; failed save keeps its dirty set | TESTED | `PowerCut_BetweenChunksAndManifest_*`, `FailedSave_KeepsItsChangesDirty_*` |
| Damaged-snapshot fallback (previous generation + journal) | TESTED | `DamagedSnapshot_FallsBackToThePreviousOne_*`, `CorruptManifest_FallsBack_AndWithNoFallbackLoadFailsLoudly` |
| Corrupt journal entries skipped and reported | TESTED | `GarbageInTheMiddleOfTheJournal_IsSkipped_AndTheRestReplays`, `TornJournalTail_IsDiscarded` |
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
| Server-side reach for pickpocketing (NPC position from the server's schedule) | TESTED | `CrimeRequests_RequireBeingThere_AndUseServerSideDisguise` |
| Ripple, radio & civic requests (post/feed/like/follow, ballot/file/donate/campaign/propose/vote/budget/register) | TESTED | `RippleAndCivicRequests_PostReadAndRegister_AndMutedPlayersCannotPost` |
| Accounts (PBKDF2, lockout, sessions) | TESTED | `Accounts_ValidateInput_HashPasswords_AndLockAfterRepeatedFailures` |
| Server directory (registration, heartbeat, listing, tickets) | TESTED | `Servers_RegisterUnderAnAccount_*`, `Stores_PersistAtomically` |
| Master HTTP API + full-stack join | TESTED | `FullStack_LoginOverHttp_TicketFromMaster_JoinTheGameServer`; CI smoke test |
| Dedicated server host (`herogame-server`) | FUNCTIONAL | CI smoke test (create, status, save, stop) |
| Unity online session (login, join, movement, remote players) | IN DEVELOPMENT | compiles; not run in Unity |
| Handler faults isolated (logged, generic error, connection survives) | TESTED | `AHandlerThatThrows_FailsOnlyThatRequest_AndTheServerCarriesOn` |
| Security audit (request surface, tickets, master API, saves) | FUNCTIONAL | `SECURITY_AUDIT.md`: 3 findings fixed with tests; TLS and session revocation open |
| Transport encryption (TLS) for game protocol and master API | PLANNED | audit finding #4 |
| Load test (30 Hz, 128 players) | PLANNED | not yet measured |
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
| Business office panel; phone (messages, map, news, radio, Ripple, bank, loans, insurance, businesses) | IN DEVELOPMENT | compiles; IMGUI placeholders |
| Radio presenter (ticker/subtitles, clip playback joined at the station's offset) | IN DEVELOPMENT | compiles; no music assets yet |
| Crime interactables (shelves, register, break-in, fence, chop shop), police desk, custody | IN DEVELOPMENT | compiles; greybox builder places them |
| Emergency unit & fire presentation, player vitals (fall damage, downed → hospital) | IN DEVELOPMENT | compiles |
| Power controller (select/charge/aim, motion effects, knockback, VFX placeholder) | IN DEVELOPMENT | compiles |
| City Hall panel (budget, council, elections, registration), Ripple phone app | IN DEVELOPMENT | compiles; IMGUI placeholders; greybox builder places a City Hall counter |
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
Combat & weapons · music, SFX and VO assets · UI Toolkit phone skin · animation set · character creator 3D preview ·
destruction · Addressables cell content · HLOD · full city · Story Mode Part Two acts II–IV.
