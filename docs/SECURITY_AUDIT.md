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
| 4 | Medium | Game-server TCP and the master HTTP API are plaintext. A network observer could read chat and a join ticket; the ticket is single-use (nonce) and short-lived, so replay needs a race, but session bearer tokens (12 h) would be exposed. | **Open.** Deploy the master behind TLS (reverse proxy or Kestrel certificate) before any public test; add TLS (`SslStream`) to the game protocol. | — |
| 5 | Low | Master session tokens are stateless HMAC tokens and cannot be revoked before expiry (no logout/ban revocation list). | **Open.** Add a revocation list keyed by token nonce, checked in `Authenticate`, and revoke on password change and ban. | — |
| 6 | Low | Rate limiting is per connection (game) and per IP on auth routes (master). A player with many connections is limited only by one-session-per-account. | Accepted for now; revisit with per-account quotas when load-testing (Phase 25 perf). | `Floods_AndMalformedFrames_*` |
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
* **Failure disclosure:** a throwing handler is logged server-side and answered with "Server error." — no exception
  text reaches the client (`AHandlerThatThrows_FailsOnlyThatRequest_AndTheServerCarriesOn`).
* **Saves:** `TypeNameHandling.None` (no type instantiation from JSON), schema-version gate, corrupt chunks and manifests
  are reported as `InvalidDataException` naming the chunk.
