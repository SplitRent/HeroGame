using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HeroGame.Core.Characters;
using HeroGame.Core.Foundation;
using HeroGame.Core.Phone;
using HeroGame.Core.Servers;
using HeroGame.Core.Simulation;
using HeroGame.Networking.Auth;
using HeroGame.Networking.Protocol;

namespace HeroGame.Networking.Server
{
    public sealed class GameServerOptions
    {
        public string ServerId = "local";
        public string ServerName = "Local server";
        /// <summary>Key that verifies join tickets for this server (derived by the master from its secret).</summary>
        public byte[] TicketKey = new byte[32];
        public IPAddress Bind = IPAddress.Loopback;
        /// <summary>0 = pick a free port (tests).</summary>
        public int Port;
        public int MaxPlayers = 64;
        public int HandshakeTimeoutMs = 10000;
        public int SendTimeoutMs = 2000;
        /// <summary>Token bucket per connection: sustained messages/second and burst.</summary>
        public float MessagesPerSecond = 40f;
        public float Burst = 80f;
        /// <summary>Violations (bad movement, flood) tolerated before a kick.</summary>
        public int MaxViolations = 25;
        public float MaxOnFootSpeed = 11f;
        public float MaxVehicleSpeed = 75f;
        public float SnapshotRadius = 400f;
        /// <summary>Nearest players replicated per snapshot (interest management; farther players are not sent).</summary>
        public int SnapshotMaxPlayers = 32;
        public float LocalChatRadius = 60f;
        public WorldPosition Spawn;
        /// <summary>When set, every connection is TLS 1.2+ with this certificate (it must carry its private key).</summary>
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate;
        /// <summary>Refuse connections that do not complete TLS (always true when a certificate is set).</summary>
        public bool RequireTls => Certificate != null;
    }

    /// <summary>Leaky-bucket rate limiter (per connection).</summary>
    public sealed class TokenBucket
    {
        private readonly float _rate;
        private readonly float _capacity;
        private float _tokens;
        private long _lastMs;

        public TokenBucket(float ratePerSecond, float capacity, long nowMs)
        {
            _rate = ratePerSecond;
            _capacity = capacity;
            _tokens = capacity;
            _lastMs = nowMs;
        }

        public bool TryTake(long nowMs, float cost = 1f)
        {
            _tokens = Math.Min(_capacity, _tokens + (nowMs - _lastMs) / 1000f * _rate);
            _lastMs = nowMs;
            if (_tokens < cost) return false;
            _tokens -= cost;
            return true;
        }
    }

    public sealed class ServerConnection
    {
        internal readonly TcpClient Tcp;
        internal System.IO.Stream Stream { get; private set; }
        private readonly object _sendLock = new object();
        private readonly int _sendTimeoutMs;

        public readonly int Id;
        public readonly string RemoteAddress;
        public readonly long ConnectedAtMs;
        public TokenBucket Bucket;
        public Ticket Ticket;
        public ServerCharacter Character;
        public bool Authenticated => Character != null;
        public WorldPosition Position;
        public float Heading;
        public float Speed;
        public EntityId Vehicle;
        public int AckSequence;
        public long LastStateMs;
        public bool PendingCorrection;
        public int Violations;
        /// <summary>Server-side disguise state (never taken from the client).</summary>
        public bool MaskOn;
        public volatile bool Closed;
        public string CloseReason = "";
        private long _bytesSent;
        private long _messagesSent;
        /// <summary>Bytes framed and written to this peer (for bandwidth budgets and the load test).</summary>
        public long BytesSent => System.Threading.Interlocked.Read(ref _bytesSent);
        public long MessagesSent => System.Threading.Interlocked.Read(ref _messagesSent);

        internal ServerConnection(int id, TcpClient tcp, long nowMs, int sendTimeoutMs)
        {
            Id = id;
            Tcp = tcp;
            Stream = tcp.GetStream();
            _sendTimeoutMs = sendTimeoutMs;
            tcp.NoDelay = true;
            tcp.SendTimeout = sendTimeoutMs;
            RemoteAddress = tcp.Client.RemoteEndPoint != null ? tcp.Client.RemoteEndPoint.ToString() : "?";
            ConnectedAtMs = nowMs;
            new System.Threading.Thread(WriteLoop) { IsBackground = true, Name = "net-write-" + id }.Start();
        }

        /// <summary>Frames waiting for the writer thread. A peer that falls this far behind is closed as too slow.</summary>
        public const int MaxQueuedFrames = 512;
        private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _outbox = new System.Collections.Concurrent.ConcurrentQueue<byte[]>();
        private readonly System.Threading.SemaphoreSlim _signal = new System.Threading.SemaphoreSlim(0);
        private int _queued;
        public int QueuedFrames => System.Threading.Volatile.Read(ref _queued);

        public string AccountId => Ticket != null ? Ticket.AccountId : "";
        public bool Encrypted { get; private set; }

        /// <summary>Runs the TLS handshake on this connection's reader thread before any frame is read.</summary>
        internal void UpgradeToTls(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, int timeoutMs)
        {
            var ssl = new System.Net.Security.SslStream(Stream, false);
            Tcp.ReceiveTimeout = timeoutMs;
            ssl.AuthenticateAsServer(certificate, false, System.Security.Authentication.SslProtocols.Tls12, false);
            Tcp.ReceiveTimeout = 0;
            lock (_sendLock) Stream = ssl;
            Encrypted = true;
        }

        /// <summary>
        /// Thread-safe, non-blocking send: the frame is queued for this connection's writer thread, so socket writes
        /// and TLS encryption never run on the simulation thread. A peer that cannot keep up is closed.
        /// </summary>
        public void Send(NetMessage message)
        {
            if (Closed) return;
            SendFrame(Wire.Frame(message));
        }

        internal void SendFrame(byte[] frame)
        {
            if (Closed) return;
            if (System.Threading.Interlocked.Increment(ref _queued) > MaxQueuedFrames)
            {
                System.Threading.Interlocked.Decrement(ref _queued);
                Close("send backlog (connection too slow)");
                return;
            }
            _outbox.Enqueue(frame);
            _signal.Release();
        }

        private void WriteLoop()
        {
            try
            {
                while (true)
                {
                    _signal.Wait(250);
                    while (_outbox.TryDequeue(out var frame))
                    {
                        System.Threading.Interlocked.Decrement(ref _queued);
                        lock (_sendLock) Stream.Write(frame, 0, frame.Length);
                        System.Threading.Interlocked.Add(ref _bytesSent, frame.Length);
                        System.Threading.Interlocked.Increment(ref _messagesSent);
                    }
                    if (Closed) break; // frames queued before Close (a Kick, a Reject) have been delivered
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
            {
                if (!Closed)
                {
                    CloseReason = "send failed";
                    Closed = true;
                }
            }
            finally
            {
                try { Tcp.Close(); } catch (Exception) { /* already gone */ }
            }
        }

        /// <summary>Marks the connection closed; the writer delivers what was already queued, then closes the socket.</summary>
        public void Close(string reason)
        {
            if (Closed) return;
            CloseReason = reason ?? "";
            Closed = true;
            _signal.Release();
        }
    }

    /// <summary>
    /// Authoritative game server (TDD §8). Sockets are read on background threads; every message is queued and
    /// handled on the simulation thread in <see cref="Pump"/>, so the world is only ever touched by one thread.
    /// Players are identified by master-signed tickets; every request runs as the connection's own character;
    /// positions are validated; floods, malformed frames and cheating attempts close only the offending connection.
    /// </summary>
    public sealed class GameServer : IDisposable
    {
        private readonly GameServerOptions _o;
        private readonly World _world;
        private readonly ModerationService _moderation;
        private readonly Func<long> _unixNow;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly ConcurrentQueue<(ServerConnection conn, NetMessage msg, string closeReason)> _inbox = new ConcurrentQueue<(ServerConnection, NetMessage, string)>();
        private readonly List<ServerConnection> _connections = new List<ServerConnection>();
        private readonly object _connectionsLock = new object();
        private readonly Dictionary<string, long> _usedNonces = new Dictionary<string, long>();
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private int _nextId;

        public readonly RequestRouter Router = new RequestRouter();

        public event Action<ServerConnection> PlayerJoined;
        public event Action<ServerConnection> PlayerLeft;
        /// <summary>Everything noteworthy (joins, kicks, violations) for the server log.</summary>
        public event Action<string> Log;

        public GameServer(GameServerOptions options, World world, ModerationService moderation, Func<long> unixNow = null)
        {
            _o = options;
            _world = world;
            _moderation = moderation ?? new ModerationService();
            _unixNow = unixNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            StandardRequests.Register(Router);
            _world.Phone.MessageReceived += OnPhoneMessage;
        }

        public World World => _world;
        public ModerationService Moderation => _moderation;
        /// <summary>World admin commands, when the host provides them (dedicated server); null disables admin.cmd.</summary>
        public AdminCommands Admin { get; set; }
        public long UnixNow => _unixNow();
        public GameServerOptions Options => _o;
        public int Port { get; private set; }
        private long NowMs => _clock.ElapsedMilliseconds;

        public List<ServerConnection> Players
        {
            get
            {
                var list = new List<ServerConnection>();
                lock (_connectionsLock)
                    foreach (var c in _connections) if (c.Authenticated && !c.Closed) list.Add(c);
                return list;
            }
        }

        public int ConnectionCount
        {
            get { lock (_connectionsLock) return _connections.Count; }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(_o.Bind, _o.Port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(() => AcceptLoop(_cts.Token));
            Log?.Invoke("Listening on " + _o.Bind + ":" + Port);
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    continue;
                }
                ServerConnection conn;
                lock (_connectionsLock)
                {
                    if (_connections.Count >= _o.MaxPlayers + 16)
                    {
                        tcp.Close();
                        continue;
                    }
                    conn = new ServerConnection(++_nextId, tcp, NowMs, _o.SendTimeoutMs) { Bucket = new TokenBucket(_o.MessagesPerSecond, _o.Burst, NowMs) };
                    _connections.Add(conn);
                }
                var reader = new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "net-read-" + conn.Id };
                reader.Start();
            }
        }

        private void ReadLoop(ServerConnection conn)
        {
            try
            {
                if (_o.Certificate != null)
                {
                    try
                    {
                        conn.UpgradeToTls(_o.Certificate, _o.HandshakeTimeoutMs);
                    }
                    catch (Exception)
                    {
                        _inbox.Enqueue((conn, null, "TLS handshake failed"));
                        return;
                    }
                }
                while (!conn.Closed)
                {
                    var msg = Wire.ReadFrame(conn.Stream);
                    if (msg == null)
                    {
                        _inbox.Enqueue((conn, null, "disconnected"));
                        return;
                    }
                    _inbox.Enqueue((conn, msg, null));
                }
            }
            catch (ProtocolException ex)
            {
                _inbox.Enqueue((conn, null, "protocol error: " + ex.Message));
            }
            catch (Exception)
            {
                _inbox.Enqueue((conn, null, "connection lost"));
            }
        }

        /// <summary>Handles every queued message on the calling (simulation) thread. Call every frame/tick.</summary>
        public void Pump()
        {
            var now = NowMs;
            while (_inbox.TryDequeue(out var item))
            {
                if (item.msg == null)
                {
                    Drop(item.conn, item.closeReason);
                    continue;
                }
                if (item.conn.Closed) continue;
                if (!item.conn.Bucket.TryTake(now))
                {
                    Violation(item.conn, "flooding");
                    continue;
                }
                try
                {
                    Handle(item.conn, item.msg, now);
                }
                catch (ProtocolException ex)
                {
                    Drop(item.conn, "protocol error: " + ex.Message);
                }
            }
            // Handshake timeouts and closed sockets.
            List<ServerConnection> snapshot;
            lock (_connectionsLock) snapshot = new List<ServerConnection>(_connections);
            foreach (var c in snapshot)
            {
                if (!c.Authenticated && !c.Closed && now - c.ConnectedAtMs > _o.HandshakeTimeoutMs) Drop(c, "handshake timeout");
                else if (c.Closed) Drop(c, c.CloseReason);
            }
            PruneNonces();
        }

        private void Handle(ServerConnection c, NetMessage msg, long now)
        {
            if (!c.Authenticated && msg.Type != MessageType.Hello && msg.Type != MessageType.Ping)
            {
                Drop(c, "not authenticated");
                return;
            }
            switch (msg)
            {
                case Hello hello: OnHello(c, hello); break;
                case Ping ping: c.Send(new Pong { Nonce = ping.Nonce }); break;
                case PlayerState state: OnState(c, state, now); break;
                case Request request: OnRequest(c, request); break;
                case ChatSend chat: OnChat(c, chat); break;
                default: Violation(c, "unexpected " + msg.Type); break;
            }
        }

        private void OnHello(ServerConnection c, Hello hello)
        {
            if (c.Authenticated)
            {
                Violation(c, "second hello");
                return;
            }
            string reason = null;
            Ticket ticket = null;
            if (hello.ProtocolVersion != Wire.ProtocolVersion) reason = "Version mismatch: server speaks protocol " + Wire.ProtocolVersion + ".";
            else if (!TicketCodec.TryVerify(_o.TicketKey, hello.Ticket, _o.ServerId, _unixNow(), out ticket, out var error)) reason = error;
            else if (_usedNonces.ContainsKey(ticket.Nonce)) reason = "Ticket already used.";
            else if (_moderation.IsBanned(ticket.AccountId, _unixNow())) reason = "You are banned from this server.";
            else if (Players.Count >= _o.MaxPlayers) reason = "Server is full.";
            if (reason != null)
            {
                c.Send(new Reject { Reason = reason });
                Drop(c, "rejected: " + reason);
                return;
            }
            _usedNonces[ticket.Nonce] = ticket.ExpiresUnix;

            // One session per account: the newest login wins.
            foreach (var other in Players)
                if (other.AccountId == ticket.AccountId)
                {
                    other.Send(new Kick { Reason = "Signed in from another location." });
                    Drop(other, "replaced by new login");
                }

            c.Ticket = ticket;
            c.Character = CharacterFor(ticket);
            c.Position = c.Character.LastPosition;
            c.LastStateMs = NowMs;
            c.Send(new Welcome
            {
                CharacterId = c.Character.CharacterId,
                ServerName = _o.ServerName,
                WorldSecond = _world.Clock.Now.TotalSeconds,
                TimeScale = _world.Clock.TimeScale,
                Position = c.Position,
                CashCents = _world.Ledger.BalanceOf(c.Character.CheckingAccount).Cents,
            });
            Log?.Invoke(ticket.DisplayName + " (" + ticket.AccountId + ") joined from " + c.RemoteAddress);
            PlayerJoined?.Invoke(c);
        }

        /// <summary>Stable per-account entity id, so the same account always gets the same character on this server.</summary>
        public static EntityId AccountEntity(string accountId) =>
            EntityId.Create(EntityKind.UserAccount, StableHash.Of("account:" + accountId) & 0x00FF_FFFF_FFFF_FFFFUL | 1UL);

        private ServerCharacter CharacterFor(Ticket ticket)
        {
            var id = AccountEntity(ticket.AccountId);
            foreach (var ch in _world.Characters.Values) if (ch.AccountId == id) return ch;
            var name = ticket.DisplayName;
            var space = name.IndexOf(' ');
            var profile = new AccountProfile
            {
                AccountId = id,
                DisplayName = name,
                Character = new CharacterIdentity { FirstName = space > 0 ? name.Substring(0, space) : name, LastName = space > 0 ? name.Substring(space + 1) : "" },
            };
            return _world.CreateCharacter(profile, _o.Spawn);
        }

        private void OnState(ServerConnection c, PlayerState s, long now)
        {
            if (s.Sequence <= c.AckSequence) return; // stale or replayed
            c.AckSequence = s.Sequence;
            var record = c.Character.Record;
            var locked = record.InCustody || c.Character.Injury == InjuryState.Hospitalized || c.Character.Injury == InjuryState.Incapacitated
                         || c.Character.Injury == InjuryState.Dead;
            var dt = Math.Max(0.05f, Math.Min(2f, (now - c.LastStateMs) / 1000f));
            c.LastStateMs = now;
            var maxSpeed = InVehicle(c, s.Vehicle) ? _o.MaxVehicleSpeed : _o.MaxOnFootSpeed;
            var moved = WorldPosition.DistanceXZ(s.Position, c.Position);
            var allowed = maxSpeed * dt * 1.5f + 2f;
            if (locked || moved > allowed || Math.Abs(s.Position.Y - c.Position.Y) > 60f)
            {
                c.PendingCorrection = true;
                if (!locked) Violation(c, "moved " + moved.ToString("0.0") + " m in " + dt.ToString("0.00") + " s");
                return;
            }
            c.Position = s.Position;
            c.Heading = s.Heading;
            c.Speed = Math.Min(Math.Abs(s.Speed), maxSpeed);
            c.Vehicle = InVehicle(c, s.Vehicle) ? s.Vehicle : EntityId.None;
            c.Character.LastPosition = s.Position;
            if (c.Vehicle.IsValid)
            {
                var v = _world.Vehicles.Get(c.Vehicle);
                if (v != null) v.Position = s.Position;
            }
        }

        private bool InVehicle(ServerConnection c, EntityId vehicle)
        {
            if (!vehicle.IsValid) return false;
            var v = _world.Vehicles.Get(vehicle);
            return v != null && (_world.Ownership.IsOwnedBy(v.Id, c.Character.CharacterId) || v.StolenBy == c.Character.CharacterId);
        }

        private void OnRequest(ServerConnection c, Request r)
        {
            Response response;
            try
            {
                response = Router.Handle(new RequestContext(this, c, r));
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A bug in one handler must not take the server down; the request simply fails.
                Log?.Invoke("Request " + r.Op + " failed: " + ex.GetType().Name + ": " + ex.Message);
                response = new Response { Success = false, Error = "Server error." };
            }
            response.RequestId = r.RequestId;
            c.Send(response);
        }

        private void OnChat(ServerConnection c, ChatSend chat)
        {
            var text = (chat.Text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > ChatSend.MaxLength) text = text.Substring(0, ChatSend.MaxLength);
            if (_moderation.IsMuted(c.AccountId, _unixNow()))
            {
                c.Send(new ChatMessage { Channel = ChatChannel.Server, FromName = "Server", Text = "You are muted." });
                return;
            }
            if (chat.Channel == ChatChannel.Server && !_moderation.Can(c.AccountId, ServerPermission.Announce))
            {
                Violation(c, "announce without permission");
                return;
            }
            var message = new ChatMessage { Channel = chat.Channel, FromName = c.Ticket.DisplayName, Text = text };
            foreach (var p in Players)
                if (chat.Channel != ChatChannel.Local || WorldPosition.DistanceXZ(p.Position, c.Position) <= _o.LocalChatRadius) p.Send(message);
        }

        private void OnPhoneMessage(ServerCharacter to, PhoneMessage m)
        {
            foreach (var p in Players)
                if (p.Character == to) p.Send(new Notice { Category = (byte)m.Category, From = m.FromName, Text = m.Body });
        }

        /// <summary>Sends each player the world state around them. Call at the snapshot rate (≈10 Hz).</summary>
        /// <summary>
        /// Sends each player the world near them. Per-player data (wanted level, name) is looked up once per broadcast,
        /// not once per viewer, and only the nearest <see cref="GameServerOptions.SnapshotMaxPlayers"/> are replicated.
        /// </summary>
        public void BroadcastSnapshots()
        {
            var players = Players;
            var n = players.Count;
            var weather = _world.Weather.State.Current;
            if (_entries.Length < n) _entries = new RemotePlayer[Math.Max(n, _entries.Length * 2)];
            for (var i = 0; i < n; i++)
            {
                var o = players[i];
                var wanted = _world.Wanted.Get(o.Character.CharacterId);
                _entries[i] = new RemotePlayer
                {
                    CharacterId = o.Character.CharacterId, Name = o.Ticket.DisplayName, Position = o.Position, Heading = o.Heading, Speed = o.Speed,
                    WantedLevel = (byte)(wanted != null ? wanted.Level : 0),
                };
            }
            var cap = Math.Min(_o.SnapshotMaxPlayers, Snapshot.MaxPlayers);
            var radiusSq = _o.SnapshotRadius * _o.SnapshotRadius;
            if (_near.Length < n) _near = new (float, int)[Math.Max(n, _near.Length * 2)];
            for (var ci = 0; ci < n; ci++)
            {
                var c = players[ci];
                var snap = new Snapshot
                {
                    WorldSecond = _world.Clock.Now.TotalSeconds,
                    Weather = (byte)weather.Kind,
                    TemperatureC = weather.TemperatureC,
                    AckSequence = c.AckSequence,
                    YourPosition = c.Position,
                    Corrected = c.PendingCorrection,
                };
                c.PendingCorrection = false;
                var count = 0;
                for (var oi = 0; oi < n; oi++)
                {
                    if (oi == ci) continue;
                    var dx = _entries[oi].Position.X - c.Position.X;
                    var dz = _entries[oi].Position.Z - c.Position.Z;
                    var d = dx * dx + dz * dz;
                    if (d <= radiusSq) _near[count++] = (d, oi);
                }
                if (count > cap) Array.Sort(_near, 0, count, NearComparer);
                for (var i = 0; i < count && i < cap; i++) snap.Players.Add(_entries[_near[i].index]);
                c.Send(snap);
            }
        }

        private RemotePlayer[] _entries = new RemotePlayer[16];
        private (float d, int index)[] _near = new (float, int)[16];
        private static readonly IComparer<(float d, int index)> NearComparer = Comparer<(float d, int index)>.Create((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.index.CompareTo(b.index));

        public ServerConnection FindByAccount(string accountId)
        {
            foreach (var p in Players) if (p.AccountId == accountId) return p;
            return null;
        }

        public void Kick(ServerConnection c, string reason)
        {
            c.Send(new Kick { Reason = reason });
            Drop(c, "kicked: " + reason);
        }

        private void Violation(ServerConnection c, string what)
        {
            c.Violations++;
            Log?.Invoke("Violation by " + (c.Authenticated ? c.Ticket.DisplayName : c.RemoteAddress) + ": " + what);
            if (c.Violations > _o.MaxViolations) Kick(c, "Too many invalid actions.");
        }

        private void Drop(ServerConnection c, string reason)
        {
            bool removed;
            lock (_connectionsLock) removed = _connections.Remove(c);
            c.Close(reason);
            if (!removed) return;
            if (c.Authenticated)
            {
                c.Character.LastOnlineSecond = _world.Clock.Now.TotalSeconds;
                if (reason != "shutdown" && !reason.StartsWith("kicked", StringComparison.Ordinal)) _world.Courts.LeftDuringPursuit(c.Character);
                Log?.Invoke(c.Ticket.DisplayName + " left (" + reason + ")");
                PlayerLeft?.Invoke(c);
            }
        }

        private void PruneNonces()
        {
            if (_usedNonces.Count < 1024) return;
            var now = _unixNow();
            var expired = new List<string>();
            foreach (var kv in _usedNonces) if (kv.Value <= now) expired.Add(kv.Key);
            foreach (var k in expired) _usedNonces.Remove(k);
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch (Exception) { /* closing */ }
            List<ServerConnection> all;
            lock (_connectionsLock) all = new List<ServerConnection>(_connections);
            foreach (var c in all)
            {
                c.Send(new Kick { Reason = "Server shutting down." });
                Drop(c, "shutdown");
            }
        }

        public void Dispose()
        {
            Stop();
            _world.Phone.MessageReceived -= OnPhoneMessage;
        }
    }
}
