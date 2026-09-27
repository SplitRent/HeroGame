# Vertical Slice Plan

**Goal (GDD §183):** prove the architecture before building the metropolis. One neighbourhood, one commercial
district, one small industrial area — fully simulated, persistent, multiplayer — with crime, police, one anomaly event
and several powers.

**Slice geography** (data: `Game/Assets/StreamingAssets/Data/layout_vertical_slice.json`, ~1.2 km × 1 km):

| District | Role in slice | Contents (current data) |
|---|---|---|
| **Eastwater** | the neighbourhood | 60-lot residential grid (Magnolia/Pecan/Lafitte), 2 apartment buildings, corner market, taqueria, laundromat, gym, auto garage, barbershop, community center, high school, park, chapel |
| **Harbor Row** | the commercial district | diner, coffee shop, pharmacy, supermarket, boutique, hardware store, bar, nightclub, gas station, bank, offices, law firm, newsroom, seafood restaurant, police precinct, fire station, hospital campus, municipal annex, lofts, rowhouses, promenade |
| **Channelside** | the small industrial area | chemical works, container terminal, logistics depot, truck stop, workers' bar, construction yard, warehouse grid, **abandoned cold-storage warehouse for sale** (conversion target) |

Current generated population for the slice: **~305 residents in ~131 households**, 20 businesses, 111 properties.

## Milestones

Each milestone has acceptance criteria that must be demonstrated (test, measurement or playable scene) before its
status in [`STATUS.md`](STATUS.md) is raised.

### VS-0 Foundation — **done in Phase 0**
- [x] Repository, folder structure, assembly definitions, coding standards, CI.
- [x] Engine-agnostic simulation core compiled by Unity and .NET from the same sources.
- [x] Data-driven content set + validation.
- [x] Persistent world with atomic transactions, journal, incremental saves; crash-recovery tests.
- [x] Headless world host with offline catch-up and inspectors.
- [x] Blender pipeline with validation, LODs, collision, Unity-correct export, procedural kit.

### VS-1 Walk the neighbourhood (Phases 1–2)
- [ ] Open project in Unity 6, run `HeroGame ▸ Setup ▸ Configure Project Settings`, HDRP Wizard ▸ Fix All,
      `HeroGame ▸ Build Greybox Vertical Slice` — scene plays with no errors. *(tooling written; needs first Unity run)*
- [ ] Third-person controller: walk/jog/sprint/crouch/jump, coyote time, camera collision. *(code written)*
- [ ] Interaction: doors, for-sale signs (buy a property end-to-end), business counters. *(code written)*
- [ ] Day/night from the world clock; weather presentation (rain, fog, wetness). *(code written)*
- [ ] Persistent NPCs visible on streets (commuters, park-goers) with the NPC inspector (F3). *(code written)*
- [ ] Replace greybox with the Blender kit (storefronts, houses, apartments, warehouse, street furniture).
- [ ] Humanoid placeholder character with locomotion animation set.
- **Accept:** 20-minute play session: walk from Magnolia Street to Harbor Row, buy the cold-storage warehouse,
  save, quit, reload — ownership and money persist; same NPCs are where their schedules say.

### VS-2 Living district (Phase 3)
- [ ] Interiors for 3 businesses and 2 home types; NPCs materialise indoors when the player enters.
- [ ] Full-tier NPC behaviours: idle/work/shop/eat/sit, greet known players, react to weather.
- [ ] NPC memory used in dialogue barks (recognises repeat customers, remembers crimes).
- [ ] Phone v1: time, map, bank balance, news feed (from `NewsDesk`), contacts.
- **Accept:** a player who works at the diner for a week is greeted by name by regulars.

### VS-3 Vehicles & traffic (Phase 4)
- [ ] Drivable car (WheelCollider), enter/exit, ownership, garage storage, fuel, damage.
- [ ] Lane graph from layout roads; ambient traffic with signals; statistical distant traffic.
- **Accept:** drive Tidewater Avenue → Refinery Road → Channelside with traffic, park, car persists after reload.

### VS-4 Property & business ownership (Phases 5–6)
- [ ] Building mode v1 on the cold-storage warehouse (walls, doors, floors, furniture, business equipment).
- [ ] Change of use: warehouse → nightclub/restaurant with a permit and template assignment.
- [ ] Business management UI: prices, staff (hire NPCs), stock, advertising, daily reports.
- [ ] Mortgages from Gulf Tidewater Bank; property tax; rent collection.
- **Accept:** convert the warehouse to a nightclub, run it for 14 in-game days (including offline days), see its
  weather-affected revenue in the phone; a storm day shows lower takings.

### VS-5 Crime & police (Phases 7–8)
- [ ] Shoplifting, burglary (doors/windows/alarms/safes), vehicle theft, store robbery.
- [ ] Witnesses/CCTV → evidence → wanted phases; police dispatch, pursuit, search, arrest; fines/jail.
- [ ] Fire/EMS response to incidents; hospital respawn per server death rules.
- **Accept:** rob Lupe's wearing a mask — police respond to the report, lose you if you break sight; Lupe remembers;
  news reports the robbery the next morning.

### VS-6 Multiplayer (Phases 9–10)
- [ ] Linux dedicated server build hosting the slice; 16 players; prediction/reconciliation for movement.
- [ ] Server-authoritative transactions (property purchase RPC with idempotency key).
- [ ] Server browser against a master server; server creation from `ServerConfig` JSON; moderation commands.
- **Accept:** two players buy adjacent houses; one robs the other while offline; the victim returns to a damaged,
  alarmed house and a police report.

### VS-7 First anomaly (Phases 11–13)
- [ ] Framework: component-driven power execution with physics (strength, telekinesis, energy projection, flight,
      blink, gravity well, speed).
- [ ] Anomaly event presentation (canal lightning in Channelside), exposure, symptoms, discovery flow.
- [ ] Interactions: electricity + wet street, telekinesis + debris, gravity + vehicles.
- **Accept:** a scheduled canal-lightning event exposes a player; symptoms over 3 days; first deliberate use; a
  witness posts on Ripple; the SIRU does *not* exist yet.

### VS-8 Slice complete
- All VS milestones accepted, performance within TDD §16 targets on the slice, save/load/crash tests green, known
  bugs triaged. Then Phase 22 world expansion begins.

## Risks
| Risk | Mitigation |
|---|---|
| Unity HDRP maintenance status | pipeline-agnostic code & content (TDD §1) |
| Netcode library limits at 128+ players | `INetSession` abstraction; NPCs not NetworkObjects; custom snapshot channel |
| JSON saves too slow for full city | binary format behind same chunk manifest (tech debt #1) |
| Procedural art reading as generic | procedural kit is first pass only; artist pass tracked per asset |
| Scope | systems before content (GDD §181): slice is small on purpose |
