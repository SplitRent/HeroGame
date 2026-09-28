# Development Report 004: combat, performance, interface, online view and water

**Scope.** This report covers the pass that followed the "done vs. not done" review and ran to completion on branch
`claude/quirky-bell-ediz4x`. It picks up where Report 003 ended.

**Environment constraints.** These are unchanged from earlier reports.

* Engine-free code was executed and tested with .NET 8.
* Unity scripts were compile-checked only, in both the editor and player configurations, with warnings treated as
  errors.
* Nothing Unity-side has been confirmed in a real editor yet. The Health Check report is still outstanding, so Unity
  rows stay **IN DEVELOPMENT**.

## 1. What was built

| Area | Delivered |
|---|---|
| Combat and weapons | <ul><li>Seven original weapons in `weapons.json`: fists, bat, crowbar, folding knife, pepper spray, stun pistol and a compact pistol.</li><li>`CombatService` makes server-authoritative attacks with deterministic hit rolls, lethal and non-lethal damage, stuns and blinding.</li><li>People react: they remember, flee home, or sometimes fight back. Gunfire clears the street and is always reported.</li><li>Hitting back within two minutes is self-defence and is not filed.</li><li>Crimes go through the existing witness, evidence, police and court path.</li><li>Guns are sold at outfitters; buying one needs a City Hall permit (fee, background check). Items and licences are journaled with the payment.</li><li>An unlicensed gun found at arrest is charged and seized. Teens are restricted in Story Mode.</li><li>Network requests, admin `give` and `permit`, the Unity combat controller, weapon counters and procedural combat sounds.</li></ul> |
| Performance | <ul><li>A shared daily census replaces repeated scans of the whole population in dispatch, elections, polls and the budget.</li><li>Emergency units are crewed only from city vehicles.</li><li>Partners are found in one pass over each relationship list.</li><li>The daily step prints per-section timings.</li><li>`SnapshotCloner` deep-copies every save chunk, so all JSON serialization happens on the save thread.</li><li>NPC copies are taken in parallel, share empty collections, and shard lists are computed once per save.</li></ul> |
| UI Toolkit interface | <ul><li>`GameUi` provides the HUD, toasts, a pause and settings screen (Audio, Controls, Display, Accessibility), and a phone with Messages, News, Bank, Properties, Inventory, Map, Radio, Ripple, Loans, Insurance and Businesses.</li><li>It uses the front end's palette. The main menu's settings edit the same file.</li><li>The IMGUI placeholders stand aside when `GameUi` is present.</li></ul> |
| Online player view | The server pushes each player's own state as `PlayerView` whenever it changes: cash, health, wanted level, messages, statement, inventory, property and businesses. The HUD and phone use it while online. `phone.read` marks messages read on the server. |
| Water | <ul><li>The layout now has water polygons with a point-in-water query.</li><li>The metro gets a Gulf coast; Coquina Key becomes a real island, reached by bridges.</li><li>Also added: the ship canal and channel, and a river.</li><li>Validation keeps buildings and roads out of the water. Water conducts for powers. The greybox draws it.</li></ul> |

## 2. Results
* NUnit: **253/253** tests pass. Service tests: **6/6** pass.
* New tests in this pass:
  * `CombatTests` (11)
  * `Combat_OverTheWire_*`
  * `PlayerView_*`
  * `TheGenericCloner_*`
  * `Water_*`
  * combat sounds in `AudioTests`
* Unity compile checks pass in both configurations.
* `.meta` integrity, script-file names, JSON validity and metro-generator freshness all pass.

## 3. Measurements (4-core container, Release)
| Measure | Before this pass | Now | Target |
|---|---|---|---|
| Daily world step, 50k NPCs | 200–264 ms | **116–137 ms** | ≤ 250 ms ✓ |
| Routine autosave (blocked), 50k NPCs | 28–38 ms | **5–7 ms** | ≤ 16 ms ✓ |
| Save right after a daily step (blocked), 50k NPCs | 140–300 ms | **91–111 ms** | ≤ 16 ms ⚠ |
| Server tick, 64 clients over TLS (with world deltas and player views) | p99 11.6 ms | **p99 8.8 ms** | 33 ms ✓ |
| Population director, metro | 1.5–1.7 ms | 1.5–1.7 ms | ≤ 2 ms ✓ |
| Population director, dense 9.3k stress layout | 2.6–3.2 ms | **1.6–1.7 ms** | ≤ 2 ms ✓ |

The save after a daily step costs about 0.8× the daily step itself. Both happen once per game day, which is about
every 48 real minutes by default.

## 4. Honest gaps
* **Unity has never run this code.** The Health Check report is the gate for every Unity row.
* **The save after a daily step still blocks for about 90–110 ms.** It is bound by copying 50,000 NPC records (empty
  collections are now shared rather than copied). Fixing it
  properly needs copy-on-write NPC state or a time-sliced snapshot.
* **The interface is functional but not art-directed.**
  * It uses the default font and text glyphs instead of icons.
  * All eleven phone apps are in UI Toolkit; savings transfers and business management still open the IMGUI
    classic panels.
* **Combat has no animation or VFX.**
  * The feedback is a flinch plus procedural sounds.
  * There are no NPC or police firearms: police still resolve through dispatch and arrest.
* **The metro has no terrain relief or hand-authored art.** Water is flat polygons. Landmarks are boxes.
* **The following can't be produced here:** art, animation, voice acting, music and play-testing. That is what stands
  between this and a finished game.

## 5. First Unity run feedback
* The first open in a real editor (Unity 6000.5) stopped in Safe Mode on `CS0104`. Newer Unity 6.x releases ship
  the .NET 8 class library, where `System.Diagnostics.ActivityKind` clashes with the game's `ActivityKind`. The
  2021.3-based compile check could not see it.
* Fixed by aliasing `Stopwatch` instead of importing `System.Diagnostics`.
* Added `dotnet build Headless/UnityCompileCheck -p:ModernBcl=true`, which also runs in CI. It compiles all Unity
  code, including the EditMode tests, against .NET 8. It reproduces the error without the fix and passes with it.
* The second open found `CS0104` on `EntityId`: Unity 6.x added `UnityEngine.EntityId`. Instead of aliasing one
  file at a time, every script now puts its `using HeroGame.*;` directives inside the namespace block. Inner usings
  take precedence over the engine and class-library imports at the top of the file, so no type name Unity or .NET
  adds later can make ours ambiguous.
* `Tools/Unity/shadow_check.py` enforces this in CI. It declares a stub for each of the 562 HeroGame type names in
  each of the 31 engine and class-library namespaces the scripts import, then compiles everything. Reverting one
  file to top-level usings makes it fail with exactly the errors Unity showed.
* Scene searches with `FindObjectsSortMode`, deprecated in newer Unity 6.x, were replaced with static registries on
  `BuildModeController` and `VehicleController`.
* Unity's serialization analyzer also warns (`UAC1001`/`UAC1009`) about dictionaries and nullable fields in Core
  data classes. These are saved with Newtonsoft JSON, not Unity serialization, so the warnings are harmless;
  `Assets/csc.rsp` now silences them.

* The third open compiled cleanly. The Health Check passed everything up to save and load, which reported a
  failure with a blank detail. The cause was the check itself: it counted residents before simulating three days,
  and a resident arrived in that time (311 → 312). The check now compares against the world as saved and names
  exactly what differs if anything does. `HealthCheckSaveAndLoad_RoundTripsExactly` runs the same scenario
  headlessly.

## 6. Next steps
1. Run the Unity Health Check and play the greybox, then fix whatever the first run shows.
2. Plan copy-on-write NPC state to remove the daily save stall.
3. Commission art for the UI, combat and metro; build UI Toolkit versions of the remaining phone apps.
4. Police use of force and armed NPC criminals, once animation exists to show them.
