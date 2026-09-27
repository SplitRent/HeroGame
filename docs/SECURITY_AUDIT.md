# Security Audit — Phase 25

**Scope:** everything a remote party can reach: the game server's TCP protocol and request catalogue
(`Networking/Server`), join tickets (`Networking/Auth`), the master server HTTP API and account/server directories
(`Headless/HeroGame.MasterServer`, `Headless/HeroGame.Services`), and the persistence layer that loads what was
written to disk. Out of scope: the Unity client itself (it is untrusted by design) and hosting infrastructure.

**Method:** each request handler was read against the core operation it calls, asking: (1) can the caller act on
something that is not theirs, (2) can they act on something they are not near, (3) can an argument be out of range,
negative, NaN, huge, or of the wrong kind, (4) can a retry double-apply, (5) can the request grow server state without
bound, (6) what does a failure reveal. Findings were fixed with a regression test, or recorded below as open.

## Findings

| # | Severity | Finding | Status | Evidence |
|---|---|---|---|---|
| 1 | High | `crime.pickpocket` took an NPC id from the client and never checked the thief was near that NPC: a player could rob anyone in the city from anywhere. | **Fixed.** The handler resolves the NPC's position from the server's own schedule and requires 12 m. | `CrimeRequests_RequireBeingThere_AndUseServerSideDisguise` |
| 2 | Medium | Ripple follow lists had no cap: one client could grow its follow list without bound (memory, save size). | **Fixed.** 500 follows per character. | `RippleService.MaxFollows` |
| 3 | Medium | A damaged snapshot (disk fault after commit) made a world unloadable, and journal compaction discarded the transactions needed to rebuild from the snapshot before it. | **Fixed.** A previous-generation fallback is kept and the journal is compacted only up to it. | `DamagedSnapshot_FallsBackToThePreviousOne_*`, `CorruptManifest_*` |
| 4 | Medium | Game-server TCP and the master HTTP API were plaintext: a network observer could read chat, tickets and 12-hour session tokens. | **Fixed.** The dedicated server speaks TLS 1.2+ by default (self-signed certificate created on first start, or `--cert`); its SHA-256 fingerprint is published by its authenticated heartbeat and returned with join tickets, and clients pin it. Plaintext clients are refused. The master serves HTTPS with `HEROGAME_CERT` (or behind a TLS proxy) and warns loudly when bound to a network interface over HTTP. | `Tls_PinnedConnectionsWork_WrongPinsAndPlaintextAreRefused`, `Heartbeat_PublishesTheServersTlsPin_AndRejectsGarbage`, CI smoke |
| 5 | Low | Master session tokens could not be revoked before expiry. | **Fixed.** Sign out (per token), sign out everywhere, and password change (signs out all sessions) — enforced in `Authenticate`. | `Sessions_CanBeSignedOut_Individually_Everywhere_AndByPasswordChange` |
| 6 | Low | Rate limiting is per connection (game) and per IP on auth routes (master). A player with many connections is limited only by one-session-per-account. | Accepted for now; revisit with per-account quotas when load-testing (Phase 25 perf). | `Floods_AndMalformedFrames_*` |
| 8 | Medium | Active wanted episodes lived only in memory: a server restart, or reloading a Story save, ended a manhunt; logging off mid-chase carried no consequence. | **Fixed.** Episodes are saved with the justice chunk; disconnecting during a pursuit files an evading charge with police-observation evidence (kicks and shutdowns excepted). | `ActiveManhunt_SurvivesARestart`, `DisconnectingDuringAChase_IsEvading_AndTheManhuntContinues` |
| 9 | Low | Power use accepted non-finite or absurd origin/aim coordinates from core callers (the network layer already bounded them). | **Fixed** in `PowerService.Use`. | `EveryPowerCombination_IsSafe` |
| 7 | Info | Player text (chat, Ripple posts, business names) has length and character limits and mute enforcement but no profanity/abuse filter. | Open (moderation tooling), not a technical vulnerability. | `RippleAndCivicRequests_*` |

## Verified controls (no change needed)

* **Ownership and authority in the core, not the handlers:** finance transfers only between the caller's own accounts;
  loan repayment and insurance claims only for the caller's loans/policies (and only for assets still owned);
  insurance only for owned assets; repairs, rentals, rent changes, change of use and building only by the owner;
  business management through `CanManage`, owner-only draws, sales and renames; council votes, proposals and budgets only
  by officeholders; donations capped; ballots once per election; admin operations through `ModerationService`
  permissions with an audit log.
* **Position authority:** power use fires from the server position; crime handlers check reach to businesses,
  properties, vehicles and (now) NPCs; filing for office requires standing at City Hall; movement is speed-checked and
  corrected.
* **Arguments:** every numeric argument is parsed with bounds (`ctx.Long/Float(min,max)`), strings with a length cap,
  enums with `TryParse`; build ops reject NaN/∞/±1000 m coordinates and oversized polygons; frames are capped at 64 KiB
  and malformed frames close only the offending connection.
* **Idempotency:** request keys are scoped per account (`acct:<id>:<key>`), duplicate keys fail in
  `TransactionProcessor`, and multi-step operations re-check state before the money moves (e.g. filing twice fails
  before a second fee).
* **Money:** every balance change is a balanced ledger transaction; the invariant is checked before every save and after
  every load; a skipped corrupt journal entry cannot unbalance the books.
* **Tickets and passwords:** HMAC-SHA256 tickets with per-server derived keys, audience, expiry and single-use nonces,
  compared in constant time; PBKDF2 password hashes with lockout; display names exclude `|` and control characters.
* **Inventory authority:** no request lets a client add, move or duplicate items; items enter inventories only from
  server-side outcomes (loot, story, purchases) and leave through server-side sales (fence, chop shop), which clear the
  goods in the same step that pays for them and refuse retried keys (`FencingTheSameLootTwice_PaysOnce`).
* **Failure disclosure:** a throwing handler is logged server-side and answered with "Server error." — no exception
  text reaches the client (`AHandlerThatThrows_FailsOnlyThatRequest_AndTheServerCarriesOn`).
* **Saves:** `TypeNameHandling.None` (no type instantiation from JSON), schema-version gate, corrupt chunks and manifests
  are reported as `InvalidDataException` naming the chunk.
