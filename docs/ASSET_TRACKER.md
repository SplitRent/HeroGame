# Asset & Placeholder Tracker

GDD §115: temporary placeholders are allowed during development **only** if tracked here and replaced.

| Asset / feature | Current state | Where | Replacement plan | Status |
|---|---|---|---|---|
| All buildings in the greybox scene | coloured boxes generated from layout data | `GreyboxWorldBuilder` | swap to Blender kit meshes per `PlaceKind`, then artist variants | PLACEHOLDER |
| Greybox materials `M_Greybox_*` | flat colour | `Assets/_Project/Generated` | real PBR materials with texture sets | PLACEHOLDER |
| Player body | capsule | greybox player rig | humanoid character + animation set (Phase 1) | PLACEHOLDER |
| NPC avatars | capsules tinted by appearance seed | `NpcAvatar_Placeholder.prefab` | modular character system driven by `AppearanceSeed` | PLACEHOLDER |
| Ground / canal water | plane / cube | greybox | terrain, water system | PLACEHOLDER |
| Rain | stretched particles | greybox | VFX Graph rain, splashes, wet shader | PLACEHOLDER |
| HUD | IMGUI `PrototypeHud` | Runtime/UI | UI Toolkit HUD (Phase 24) | PLACEHOLDER |
| Build mode UI | IMGUI side panel in `BuildModeController`; greybox cube walls/furniture from `LayoutRenderer` | Runtime/Building | UI Toolkit build palette + catalog prefabs | PLACEHOLDER |
| Business office / phone apps | IMGUI `BusinessPanel`, `PhonePanel` (messages, map, news, radio, Ripple, bank, loans, insurance, businesses) | Runtime/UI | UI Toolkit phone skin + office screen | PLACEHOLDER |
| Police desk / court UI | IMGUI `JusticePanel` | Runtime/UI | UI Toolkit screens (Phase 24) | PLACEHOLDER |
| City Hall UI (budget, council, elections, registration) | IMGUI `CivicPanel`; invisible counter trigger (`CityHallDesk`) | Runtime/UI, Runtime/Civic | UI Toolkit civic screens, City Hall interior with clerk NPC | PLACEHOLDER |
| Ripple social app | IMGUI `RippleApp` tab inside the phone panel; no avatars or images | Runtime/UI | phone UI app with profile pictures and media (Phase 23) | PLACEHOLDER |
| NPC Ripple posts | template lines (`ripple_templates.json`) filled from history records | Data | larger, district-voiced line sets; writer pass | FIRST PASS |
| Calendar events, ordinances | original data (`calendar_events.json`, `ordinances.json`) | Data | expand with writers; no real-world political parties or officials | FIRST PASS |
| Story dialogue box, objective HUD, cutscenes | IMGUI `StoryPresenter`; cutscenes are camera cuts with subtitles | Runtime/Story | UI Toolkit dialogue UI, Timeline cutscenes, voice and animation | PLACEHOLDER |
| Story cast appearance | generic NPC capsules | — | authored character models for the cast (Rosa, Pilar, Lupe, Dee, Mai, Rafa, Coach, Abernathy…) | PLACEHOLDER |
| Police car / fire engine / ambulance | tinted boxes with a point-light bar (`*_Greybox.prefab`) | Generated/ | modelled service vehicles with livery, sirens | PLACEHOLDER |
| Fire | single orange particle system | `Fire_Placeholder.prefab` | layered fire/smoke VFX Graph | PLACEHOLDER |
| Power effects | one burst particle system tinted per element | `PowerImpact_Placeholder.prefab` | per-archetype VFX Graph effects, audio, animation | PLACEHOLDER |
| Crime interaction points, fence & chop-shop contacts, holding cell | invisible trigger boxes placed by `GreyboxWorldBuilder` | Runtime/Crime | authored interiors, NPC fence characters, animations | PLACEHOLDER |
| Street furniture in scene | Blender kit prefab from `Resources/Props/<Mesh>` when imported, else greybox primitives; traffic signal has no kit mesh yet | `DestructiblePresenter` | kit meshes into Resources/Props; model a traffic signal; destruction states (bent pole, cracked glass) | PLACEHOLDER |
| Destruction debris & hydrant spray | grey cubes with rigidbodies; one particle cone | `DestructiblePresenter` | fracture meshes, VFX Graph water and dust | PLACEHOLDER |
| Dev console / inspector | IMGUI | dev builds only | stays IMGUI (developer-only) | OK (not shipped) |
| Main menu backdrop | flat colour | MainMenu.uss | live city flythrough / key art | PLACEHOLDER |
| Server list | mock JSON | `server_list_mock.json` | master server query (Phase 9) | PLACEHOLDER |
| Procedural kit: 3 storefronts, office, warehouse, 3 shotgun houses, apartments | blockout-grade generated meshes, flat materials, 4 LODs + UCX | `Tools/Blender` (exported by CI) | artist pass: trims, detail normals, signage, interiors | FIRST PASS |
| Procedural kit: street light, bench, hydrant, bollard, bus shelter, dumpster | generated meshes, 3 LODs + UCX | `Tools/Blender` | artist pass | FIRST PASS |
| Names of people | generic common first/last names | `names.json` | expand pools; no real public figures | OK |
| Business/brand names | original | layout JSON | — | OK |
| Radio music | original track metadata only (`radio_stations.json`, 25 fictional songs/artists); `RadioPresenter` plays `Resources/Radio/<trackId>` when present — none exist | Data, Runtime/UI | commission original tracks at the listed lengths | MISSING |
| Radio host/news voice, SFX, VO | subtitles only | `RadioPresenter` ticker | original VO recordings / TTS pass, SFX library | MISSING |
| Working title "SECOND LIFE" | trademark conflict | `GameInfo.WorkingTitle` | final title before any public build | MUST REPLACE |
