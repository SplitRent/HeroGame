# Networking, accounts and servers

Status: see `STATUS.md` (protocol, server, master and tickets are TESTED headlessly; the Unity client is compile-checked only).

## Topology

```
 player client (Unity) ──HTTPS──▶ master server (herogame-master)      accounts, server list, join tickets
        │                               ▲
        │ TCP game protocol             │ register (owner's session) · heartbeat (server key)
        ▼                               │
 game server (herogame-server) ─────────┘    one authoritative world per server; saves + journal on disk
```

* **Master server** (`Headless/HeroGame.MasterServer`, ASP.NET Core minimal API over `HeroGame.Services`):
  global accounts (PBKDF2-SHA256, 210k iterations, lockout after 5 failures), the public server list (servers
  appear only while heartbeating), and join tickets.
* **Game server** (`Headless/HeroGame.Server`, library `Game/Assets/_Project/Networking`): hosts one world
  (the same core the single-player game runs), accepts clients over TCP, handles every state change.
* **Client** (`Networking/Client`, Unity glue in `Runtime/Online`): signs in, asks the master for a ticket,
  connects, streams movement, sends requests.

## Trust model

| Threat | Mitigation |
|---|---|
| Forged identity | Clients never send an account id. They present a **join ticket**: HMAC-SHA256 over (account, name, server, expiry, nonce). |
| Community server minting tickets elsewhere | Each server gets `key = HMAC(masterSecret, "server-key|" + serverId)`. It can verify its own tickets only; it never sees the master secret. |
| Ticket replay | Nonces remembered until expiry; tickets live 60 s. |
| Acting as another player | Requests run as the connection's character; ids in arguments only name *targets*. Idempotency keys are scoped per account. |
| Speed/teleport hacks | Server keeps the authoritative position; moves faster than the on-foot/vehicle limits are refused and the client is corrected; repeated violations → kick. |
| Acting at a distance | Crime, build and similar requests check proximity against the server position. |
| Floods, malformed or huge frames | Per-connection token bucket; 64 KiB frame cap checked before allocation; bounded strings/maps; NaN/∞ rejected; a bad frame closes only that connection. |
| Idle sockets | Unauthenticated connections are dropped after 10 s. |
| Admin abuse | Role ranks (owner > admin > moderator > player); every kick/ban/mute/grant is recorded in the moderation log, persisted with the save. |
| Money duplication | All money moves through the core transaction processor (double-entry, journaled); the protocol adds no money paths. |

## Protocol

Frames: `[uint32 length][uint16 type][payload]`, little-endian, version `Wire.ProtocolVersion`.
Messages: `Hello/Welcome/Reject`, `Ping/Pong`, `PlayerState` (client → server, ~10 Hz), `Snapshot`
(server → client, ~10 Hz: clock, weather, your authoritative position/corrections, nearby players),
`WorldDelta` (server → client, every 500 ms when something changed; the full state right after `Welcome`),
`LayoutData` (a rebuilt building's layout, on request), `PlayerView` (the player's own cash, health, wanted level,
messages, statement, inventory, property and businesses as JSON, sent on join and whenever it changes),
`Request/Response` (op name + bounded string map), chat, phone `Notice` pushes, `Kick`.

Request ops (see `StandardRequests`): `me.status`, `property.buy|mortgage|repair`, `build.commit`,
`business.buy|start|price|wage|ads|hire|fire|withdraw|invest|restock`, `finance.transfer|savings|loan|repay`,
`insurance.buy|claim`, `character.mask`, `crime.shoplift|rob|burgle|steal_vehicle|pickpocket|fence`,
`justice.bail|fines|attorney|plea|surrender`, `power.use`, `combat.attack` (target npc|character|none; positions come
from the server), `weapons.buy|ammo` (at the shop), `civic.firearm_permit` (at City Hall), `phone.read`, `radio.now`,
`property.layout`, `prop.impact`, `ripple.post|feed|like|follow` (posting is refused while
muted; the author name comes from the signed ticket), `civic.register_powers|ballot|file|donate|campaign|propose|council_vote|budget`
(filing requires standing at City Hall), `admin.kick|ban|unban|mute|unmute|grant|role`.

## Shared world replication

`WorldReplicator` diffs the authoritative world against the view it last published, so no gameplay code has to
announce changes: broken street props (state), fires (position, intensity; intensity changes under 0.05 are not
sent), and for every property that differs from the default — a player owner, a sale listing and price, damage, or a
rebuilt layout (a version number bumped whenever the copy-on-write layout object changes). A property returning to
all defaults is sent once with defaults and dropped. A joining player first gets the full view (split across frames:
≤ 2,000 props, 256 fires, 1,200 properties each, under the 64 KiB frame), then deltas. Rebuilt layouts are fetched on
demand with `property.layout` and arrive as `LayoutData` (JSON of the layout, ≤ 60,000 bytes). On the client,
`GameClient.World` (`ReplicatedWorld`) holds the mirror and raises change events; fire, prop, for-sale-sign and
build-mode presenters use it while online. `Welcome` carries the server's layout id and the client warns when its
local map differs.

## Transport security

* **Game protocol:** TLS 1.2+ (`SslStream`) on by default in `herogame-server`. Without `--cert FILE.pfx` a self-signed
  RSA-2048 certificate is created on first start as `server-tls.pfx` in the save directory (owner-only permissions) and
  reused. Its SHA-256 fingerprint is printed at start-up, published through the server's authenticated heartbeat, and
  returned by the master with every join ticket; clients pin it (`ClientTls.PinnedFingerprint`) so self-signed community
  servers are still protected against a man in the middle. `--no-tls` exists for local debugging only.
* **Master API:** HTTPS with `HEROGAME_URLS=https://…` and `HEROGAME_CERT`/`HEROGAME_CERT_PASSWORD`, or plain HTTP on
  loopback behind a TLS reverse proxy. It warns at start-up when it would serve HTTP on a network interface.
* **Sessions:** `POST /api/accounts/logout`, `/api/accounts/logout-all`, `/api/accounts/password` (current + new; signs out
  every session).

## Running

```bash
# master
export HEROGAME_MASTER_SECRET=$(openssl rand -base64 48)   # keep secret; rotate = all server keys change
HEROGAME_DATA=./master-data HEROGAME_URLS=http://0.0.0.0:5080 herogame-master

# register a server (once) with an account session, e.g. via MasterClient.RegisterServer → ServerId + ServerKey
herogame-server --save ./world --port 27015 --name "Harbor Nights" \
                --master http://master:5080 --server-id srv_xxx --server-key BASE64 --owner acc_xxx

# LAN / development without a master: the console command `ticket ACCOUNT NAME` prints a join ticket
herogame-server --save ./world --dev-secret $(openssl rand -base64 48) --owner dev
```

## Known limitations (honest list)

* The Unity client has not been run against a server inside Unity yet (compile-checked only).
* Shared world changes are replicated (see above), but businesses, vehicles, NPC positions and phone/bank screens
  are still read from the client's local presentation world or through requests; NPC/weather/time agree because
  they are deterministic from the shared seed and clock. A player-facing bank/business view over requests is next.
* The game protocol is TLS with pinned fingerprints (see Transport security); movement still rides TCP, so a
  DTLS/UDP channel for movement remains an option under heavy loss.
* HMAC tickets mean the master must be trusted with the secret; moving to asymmetric signatures would let game
  servers verify without any shared key.
