# Live Status

Labels (GDD §148): **PLANNED · PROTOTYPING · IN DEVELOPMENT · FUNCTIONAL · POLISHED · TESTED · OPTIMIZED · COMPLETE**

Rules: a label is raised only with evidence. "Evidence" column names the test, measurement or tool that justifies it.
*Code written but never executed inside Unity* is at most **IN DEVELOPMENT** — there is no editor in the development
environment; Unity scripts are verified by compiling against Unity reference assemblies only. The project owner has
opened the project in Unity, but no Health Check report or Console log has confirmed a successful run yet, so no
Unity row is raised.

_Last updated: combat and weapons, performance pass, UI Toolkit interface, online player view, water. See `reports/DEV_REPORT_004_COMBAT_UI_PERFORMANCE.md`._

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
| Destructible street furniture (lights, signals, hydrants, benches, shelters, dumpsters, bollards) placed from roads/places; damage states | TESTED | `StreetFurniture_IsPlacedFromRoadsAndPlaces_Deterministically`, `DamageStates_AndTheirConsequences` |
| Destruction consequences: dark streets cut witness visibility at night, broken hydrants flood (wet) streets, dead signals slow intersections | TESTED | `DamageStates_AndTheirConsequences` |
| Vehicle impacts (server-validated online), power blasts and hurricane winds break props | TESTED | `VehicleImpacts_*`, `PropImpacts_NeedAVehicle_AndBeingThere`, `HurricaneWinds_*` |
| Structural collapse: EMS call, leases end with deposits returned, businesses forced shut, debris; city rebuilds non-player buildings after 60 days | TESTED | `BuildingCollapse_*` |
| Recovery: public-works crews sized by budget repair by priority, paid from the treasury | TESTED | `PublicWorks_RepairsByPriority_*` |
| Destructible presenter (kit prefab or greybox, tilt when damaged, physics debris, hydrant spray) | IN DEVELOPMENT | compiles; not run in Unity |
| Procedural placeholder audio (music loops per track, sirens, engines, rain, wind, thunder, 5 ambience beds, UI, power charge/impact, voice babble) | TESTED | `EverySound_IsFinite_Audible_AndUnclipped`, `Music_IsDeterministicPerTrack_*`, `Sirens_SoundLikeTheirService`, `Babble_*` |
| Soundscape mixing from world state (district bed, day/night, weather, indoor muffling, crowds, sirens of units on real calls) | TESTED | `Soundscape_FollowsTheWorld` |
| Audio director, vehicle engines, radio playback (procedural music/host voice), dialogue/UI/phone/power sounds | IN DEVELOPMENT | compiles; not run in Unity |
| Admin/debug commands shared by dev console, server console (`admin`, `as ACCOUNT`) and `admin.cmd` (WorldAdmin, audited): spawn NPCs/vehicles, events, weather, time, money, ownership, powers, anomalies, police, elections, ordinances, businesses, props, status | TESTED | `AdminCommandTests` (4), `AdminCommands_RequireWorldAdmin_AndAreAudited` |
| World inspector: active events, emergencies, manhunts, elections, broken props, network state | IN DEVELOPMENT | compiles (F3 overlay) |
| Youth curfew enforced in NPC schedules (minors home in the window; adults unaffected; repeal lifts it) | TESTED | `YouthCurfew_KeepsMinorsHome_AndLiftsWithRepeal` |
| Weekend late nights for ages 15–29 (Fri/Sat, past midnight, extraversion-driven) | TESTED | exercised by the curfew test; offline/live equivalence tests still pass |
| Campaign funds carry over between a player's campaigns | TESTED | `LeftoverCampaignFunds_CarryOverToTheNextRun` |
| Full Port Arden metro layout: 19 districts (slice embedded verbatim), landmarks, services, 302 businesses, ~7,500 places, one connected road network, generated deterministically | TESTED | `FullMetro_Validates_*`, `FullMetro_HoldsAboutFiftyThousandResidents_OnOneConnectedRoadNetwork`; CI `generate_metro.py --check` |
| 50k-NPC simulation (daily step, director, catch-up) | OPTIMIZED | `herogame-world bench --layout layout_port_arden.json` (CI): 116–137 ms/day at 50,400 NPCs (target 250), per-section timings printed; shared daily census; director 1.2 ms (metro) / 1.6–1.7 ms (dense stress layout) |
| Combat: original weapons (fists, bat, crowbar, knife, pepper spray, stun pistol, compact pistol), server-authoritative attacks (reach, cooldown, ammunition, PvP switch), deterministic hits, lethal vs non-lethal, stun/blind | TESTED | `CombatTests` (11): `APunch_*`, `Reach_Cooldowns_*`, `FistsAndNonLethalWeapons_NeverKill`, `StunPistol_*`, `AShooting_CanKill_IsAHomicide_*` |
| Combat consequences: victims remember, flee or fight back; gunfire clears streets and is always reported; self-defence not filed; assault/aggravated assault/homicide through the justice path; unlicensed guns charged at arrest | TESTED | `HittingBack_IsSelfDefence_*`, `ArrestedWithAnUnlicensedGun_*`, `PlayersCanFight_OnlyWherePvpIsAllowed_*` |
| Weapon shops, ammunition, firearm permits (fee, background check, 5 years); items and licenses ride in the payment's journal entry; teen restriction | TESTED | `Firearms_NeedAPermit_*`, `AWeaponPurchase_SurvivesACrash_*`, `Teenagers_CannotBuyWeapons*` |
| Street encounters: muggings of present players (night, district crime, lighting, visible wealth); comply / refuse-and-fight (self-defence) / run; real NPC suspects reported, some arrested with records and custody; server switch | TESTED | `StreetCrimeTests` (5), `StreetEncounters_ShowInThePlayerView_AndResolveOverTheWire` |
| Water: coast, Coquina Key island, sound and passes, ship canal and channel, river; validator keeps buildings and roads out of it; water conducts for powers | TESTED | `Water_ShapesTheCoast_CoquinaKeyIsAnIsland_*` |
| Player UI logic: settings (validated, persisted, unit/clock formatting), notification queue (folding, priority, bounds), property portfolio (equity, rent, net, alerts), inventory (stacks, stolen/contraband) | TESTED | `PlayerUiTests` (5) |

## Story Mode
| System | Status | Evidence / notes |
|---|---|---|
| Story framework (missions, objectives, branching, dialogue trees, effects/conditions, cutscene requests) | TESTED | `Dialogue_*`, `RafasFavor_RefusingSkipsTheDeliveryBranch` |
| Story content validation (ids, places, branches, scripted effects) | TESTED | `StoryContent_PassesValidation`, `Validator_CatchesWriterMistakes` |
| Cast as real NPCs in a 2026 world; teen restrictions | TESTED | `Begin_CastsRealPeople_InARealWorld_In2026` |
| Part One "Magnolia Street" (7 missions) + four-year time jump (simulated) + Part Two opener | TESTED | `PartOne_PlaysThrough_Isadora_AndTheFourYearJump` (scripted playthrough) |
| Story save/resume | TESTED | `StoryState_SurvivesSaveAndLoad_MidMission` |
| Part Two acts II–IV and finale (11 missions: Static, Tests, Witness, Follow the Money, Shells, Calloway's Canal, The Ordinance, Campaign Trail, Abernathy, Landfall, Who He Is) with three endings (saved / sold / split) | TESTED | `PartTwo_StandingUpToCalloway_*`, `PartTwo_TakingCallowaysDeal_*` (scripted playthroughs through real systems: fire dispatch, council vote, election, storm, chemical incident, property transfers) |
| Story verbs for Part Two (power stage/uses, reputation, ordinances, elections, disasters, fires, registration, opinion, transfers, endings) with load-time validation | TESTED | `StoryContent_PassesValidation` |
| Full story sandbox (Phase 18): every system open after the jump; after the finale the sandbox continues and the ending is recorded in history | TESTED | `PartTwo_StandingUpToCalloway_*` (post-credits assertions) |
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
| City layout recorded in saves; wrong layout refused; hosts reopen on the recorded layout | TESTED | `Saves_RememberTheirLayout_AndRefuseToOpenOnAnotherMap` |
| All save serialization off the simulation thread (generic snapshot cloner, parallel NPC copies, copy-on-write layout shards) | TESTED | `TheGenericCloner_CopiesEverySavedKindOfData_*`, `NpcAndHouseholdSnapshots_*`, `EditingOneBuilding_*`, `SavesFromBeforeLayoutShards_*`; bench at 50k: routine autosave 5–7 ms blocked ✓, save right after a daily step 138–180 ms blocked (was ~860) ⚠ target 16 ms |
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
| Security audit (request surface, tickets, master API, saves) | FUNCTIONAL | `SECURITY_AUDIT.md`: 7 findings fixed with tests; per-account quotas and text moderation open |
| Transport encryption (TLS with pinned fingerprints) for the game protocol; HTTPS option and warning for the master | TESTED | `Tls_PinnedConnectionsWork_WrongPinsAndPlaintextAreRefused`, `Heartbeat_PublishesTheServersTlsPin_*`; CI smoke runs with TLS |
| Session sign-out, sign-out everywhere, password change | TESTED | `Sessions_CanBeSignedOut_Individually_Everywhere_AndByPasswordChange` |
| Persisted manhunts; disconnecting mid-chase is evading | TESTED | `ActiveManhunt_SurvivesARestart`, `DisconnectingDuringAChase_*` |
| Load test (30 Hz, 128 players) | TESTED | `herogame-server loadtest` (CI: 64 clients over TLS); 128 clients: p99 21 ms/tick, 12 KiB/s per client; async per-connection writers, 32-nearest snapshot interest |
| Background autosave (snapshot on the simulation thread, writes off it; failed writes retried) | TESTED | `BackgroundSave_Commits_*`; bench: routine autosave 12.9 ms blocked |
| Online player view (cash, health, wanted, messages, statement, inventory, property, businesses) pushed when changed; phone.read | TESTED | `PlayerView_ArrivesOnJoin_FollowsTheServer_AndOnlyTheirOwn`; 64-client TLS load test p99 8.8 ms |
| Online finance: credit, loans, policies and insurable assets in the player view; finance.quote, insurance.quote, insurance.cancel | TESTED | `FinanceOverTheWire_QuotesCoverAndCancellation_ShowUpInThePlayerView` |
| Combat and weapon requests over the network (server positions only, PvP switch, shop proximity) | TESTED | `Combat_OverTheWire_UsesTheServersPositions_AndRespectsPvp` |
| Replication of shared world changes (broken props, fires, player ownership, sale listings, damage, rebuilt buildings on demand); full state on join | TESTED | `WorldChanges_ReachEveryone_LateJoinersGetTheFullState_AndRebuiltBuildingsCanBeFetched`, `WorldDeltas_RoundTrip_AndHostileCountsAreRejected`; load test unchanged |
| `--layout` for the dedicated server and world host | FUNCTIONAL | CI metro bench; manual `new --layout` → `run` reopens on the metro |

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
| Health check (HeroGame ▸ Health Check): Unity version, HDRP, input, colour space, data, story, simulation smoke, scenes | IN DEVELOPMENT | compiles; waiting on the first report from a real editor |
| UI Toolkit in-game interface: HUD (clock, weather, cash, health, wanted, phone badge, weapon, prompt, subtitles, FPS), toasts, pause/settings (Audio, Controls, Display, Accessibility), phone (all eleven apps: Messages, News, Bank, Properties, Inventory, Map, Radio, Ripple, Loans, Insurance, Businesses; online data and actions go through the server) | IN DEVELOPMENT | compiles; uses tested view-models; IMGUI placeholders remain as fallback |
| Street encounter presenter (mugger avatar, E to hand over, HUD card with countdown) | IN DEVELOPMENT | compiles |
| Combat controller (X cycles weapons, attack the person in front; offline and online), weapons counters in shops, permit desk, NPC flinch, procedural combat sounds | IN DEVELOPMENT | compiles |
| Online presentation from the replica (fires, props, for-sale signs, rebuilt interiors in build mode) | IN DEVELOPMENT | compiles |
| Metro greybox scene (HeroGame ▸ Build Greybox Full Metro) and `GameBootstrap.LayoutFile` | IN DEVELOPMENT | compiles; heavy scene, not yet built in Unity |

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
Recorded music, SFX and VO (procedural placeholders exist) · final UI art (typeface, icons, motion) and UI Toolkit versions of the rarer phone apps · animation set (combat, hit reactions) · character creator 3D preview ·
Addressables cell content · HLOD · hand-authored district art, terrain and water for the metro (the metro exists as data and greybox).
