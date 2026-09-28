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
| Main menu backdrop | flat colour | FrontEnd.uss | live city flythrough / key art | PLACEHOLDER |
| Server list | mock JSON | `server_list_mock.json` | master server query (Phase 9) | PLACEHOLDER |
| Procedural building kit (20 buildings): 4 storefronts (1–3 floors), 3 offices (4/8/16 floors), warehouse, 3 shotgun houses, 5 detailed suburban houses (`house_detail.py`, 10–17k tris). These are modelled in 3D rather than painted on:
  * individually modelled tapered lap-siding boards cut around openings and up into the gables
  * boxed eaves with grooved soffit panels, vent strip, fascia, frieze, K-style gutters and downspouts
  * a sloped roof deck with rake boards, stepped shingle courses and a ridge cap
  * recessed double-hung windows with sashes, muntins, casings, drip caps, sills and louvered shutters
  * a six-panel door and a sectional garage door
  * a gabled portico with posts, railing and balusters
  * corner boards, a brick chimney with crown, and an AC unit, 2 apartment blocks, gas station, church | generated midpoly meshes with 16 procedural tileable PBR textures (brick, stucco, concrete, siding, shingles, gravel roof, corrugated/standing-seam metal, wood: base colour + normal, 512 px per 2 m); 4 LODs + UCX; committed under `Art/Environment` and fitted to every lot by the greybox builder | `Tools/Blender` (`kit`, `textures`), `KitMaterials`, `KitBuildings` | artist pass: recessed windows, trims, signage, weathering, interiors, roof clutter | FIRST PASS |
| Procedural kit: street light, bench, hydrant, bollard, bus shelter, dumpster | generated meshes, 3 LODs + UCX | `Tools/Blender` | artist pass | FIRST PASS |
| Names of people | generic common first/last names | `names.json` | expand pools; no real public figures | OK |
| Business/brand names | original | layout JSON | — | OK |
| Radio music | procedural 8-bar arrangement per track (`Synth.MusicLoop`, seeded by track id and station genre), looped for the track's length; `Resources/Radio/<trackId>` overrides it | Core/Audio, `RadioPresenter` | commission original tracks at the listed lengths | PLACEHOLDER |
| Voices (dialogue, radio hosts) | procedural syllable babble in each speaker's own pitch under the subtitles | `Synth.Babble` | VO recordings | PLACEHOLDER |
| Sirens, engines, rain, wind, thunder, ambience beds, UI, power sounds | procedural synthesis; any clip at `Resources/Audio/<name>` replaces its placeholder | `Synth`, `AudioDirector`, `VehicleAudio` | recorded/licensed-original SFX library | PLACEHOLDER |
| Full metro city plan (16 non-slice districts) | procedural street grids, parcel blocks and packed service/business lots from `Tools/World/generate_metro.py`; no terrain, water or coastline shapes | `layout_port_arden.json`, `PortArden_Metro_Greybox` | hand-authored district plans, terrain/water, bespoke landmark buildings | PLACEHOLDER |
| Generated business and street names (metro) | combinatorial original names (surnames/adjectives/nouns); no real brands | `generate_metro.py` | writer pass for flavour | OK |
| In-game UI: HUD, toasts, pause/settings, phone (Messages, News, Bank, Properties, Inventory, Map) | UI Toolkit (`GameHud.uxml`, `Game.uss`) in the front end's palette, default font, text glyphs (★ ▲ $) in the HUD; phone app icons drawn in code (`PhoneIcons`: white glyphs on coloured tiles); `Controls.uss` dark-styles Unity's built-in fields; all eleven phone apps (Messages, News, Bank, Properties, Inventory, Map, Radio, Ripple, Loans, Insurance, Businesses) in UI Toolkit, online and offline; the IMGUI classic phone remains for savings transfers and business management | `GameUi`, `PhonePanel`, `BusinessPanel` | final typeface, icon set, motion design | PLACEHOLDER |
| Greybox sky and exposure (HDRP) | physically based sky, automatic exposure and light fog in a generated global volume (`Generated/GreyboxSky.asset`) | `GreyboxWorldBuilder.BuildSkyVolume` | art-directed time-of-day lighting, clouds, post-processing | PLACEHOLDER |
| Working title "SECOND LIFE" | trademark conflict | `GameInfo.WorkingTitle` | final title before any public build | MUST REPLACE |
