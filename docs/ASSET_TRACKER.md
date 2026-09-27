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
| Business office / bank & insurance apps | IMGUI `BusinessPanel`, `FinancePanel` | Runtime/UI | phone apps + office screen (Phases 23–24) | PLACEHOLDER |
| Police desk / court UI | IMGUI `JusticePanel` | Runtime/UI | UI Toolkit screens (Phase 24) | PLACEHOLDER |
| Story dialogue box, objective HUD, cutscenes | IMGUI `StoryPresenter`; cutscenes are camera cuts with subtitles | Runtime/Story | UI Toolkit dialogue UI, Timeline cutscenes, voice and animation | PLACEHOLDER |
| Story cast appearance | generic NPC capsules | — | authored character models for the cast (Rosa, Pilar, Lupe, Dee, Mai, Rafa, Coach, Abernathy…) | PLACEHOLDER |
| Police car / fire engine / ambulance | tinted boxes with a point-light bar (`*_Greybox.prefab`) | Generated/ | modelled service vehicles with livery, sirens | PLACEHOLDER |
| Fire | single orange particle system | `Fire_Placeholder.prefab` | layered fire/smoke VFX Graph | PLACEHOLDER |
| Power effects | one burst particle system tinted per element | `PowerImpact_Placeholder.prefab` | per-archetype VFX Graph effects, audio, animation | PLACEHOLDER |
| Crime interaction points, fence & chop-shop contacts, holding cell | invisible trigger boxes placed by `GreyboxWorldBuilder` | Runtime/Crime | authored interiors, NPC fence characters, animations | PLACEHOLDER |
| Dev console / inspector | IMGUI | dev builds only | stays IMGUI (developer-only) | OK (not shipped) |
| Main menu backdrop | flat colour | MainMenu.uss | live city flythrough / key art | PLACEHOLDER |
| Server list | mock JSON | `server_list_mock.json` | master server query (Phase 9) | PLACEHOLDER |
| Procedural kit: 3 storefronts, office, warehouse, 3 shotgun houses, apartments | blockout-grade generated meshes, flat materials, 4 LODs + UCX | `Tools/Blender` (exported by CI) | artist pass: trims, detail normals, signage, interiors | FIRST PASS |
| Procedural kit: street light, bench, hydrant, bollard, bus shelter, dumpster | generated meshes, 3 LODs + UCX | `Tools/Blender` | artist pass | FIRST PASS |
| Names of people | generic common first/last names | `names.json` | expand pools; no real public figures | OK |
| Business/brand names | original | layout JSON | — | OK |
| Music, radio, VO | none | — | original commissions (Phase 23) | MISSING |
| Working title "SECOND LIFE" | trademark conflict | `GameInfo.WorkingTitle` | final title before any public build | MUST REPLACE |
