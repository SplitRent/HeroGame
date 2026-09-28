using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace HeroGame.Tests
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Property;
    using HeroGame.Core.Servers;
    using HeroGame.Core.Simulation;
    using HeroGame.Networking.Auth;
    using HeroGame.Networking.Client;
    using HeroGame.Networking.Protocol;
    using HeroGame.Networking.Server;

    public class NetworkingTests
    {
        private static readonly byte[] MasterSecret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        private const string ServerId = "srv-test";

        private World _world;
        private GameServer _server;
        private ModerationService _moderation;
        private readonly List<GameClient> _clients = new List<GameClient>();

        [SetUp]
        public void SetUp()
        {
            _world = WorldGenerator.Create("net", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            _moderation = new ModerationService();
            _server = new GameServer(new GameServerOptions
            {
                ServerId = ServerId, ServerName = "Test City", TicketKey = TicketCodec.DeriveServerKey(MasterSecret, ServerId),
                Spawn = new WorldPosition(-200f, 0f, 40f), HandshakeTimeoutMs = 1500,
            }, _world, _moderation);
            _server.Start();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
            _server.Dispose();
        }

        private static string TicketFor(string account, string name, string audience = ServerId, long ttl = 300, byte[] key = null) =>
            TicketCodec.Issue(key ?? TicketCodec.DeriveServerKey(MasterSecret, audience),
                new Ticket { AccountId = account, DisplayName = name, Audience = audience, ExpiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ttl, Nonce = TicketCodec.NewNonce() });

        private void Until(Func<bool> condition, int timeoutMs = 3000)
        {
            var start = Environment.TickCount;
            var lastSnapshot = 0;
            while (!condition())
            {
                if (Environment.TickCount - start > timeoutMs) Assert.Fail("Timed out waiting for condition.");
                _server.Pump();
                if (Environment.TickCount - lastSnapshot > 50)
                {
                    _server.BroadcastSnapshots();
                    lastSnapshot = Environment.TickCount;
                }
                foreach (var c in _clients) c.Pump();
                Thread.Sleep(2);
            }
        }

        private GameClient Join(string account, string name, string ticket = null)
        {
            var c = new GameClient();
            _clients.Add(c);
            c.ConnectAsync("127.0.0.1", _server.Port, ticket ?? TicketFor(account, name)).Wait(3000);
            Until(() => c.State == ClientState.Connected || c.State == ClientState.Disconnected || c.RejectReason.Length > 0);
            return c;
        }

        private Response Call(GameClient c, string op, Dictionary<string, string> args = null, string key = null)
        {
            var task = c.Request(op, args, key);
            Until(() => task.IsCompleted);
            return task.Result;
        }

        // ------------------------------------------------------------------ protocol

        [Test]
        public void Wire_RoundTripsMessages_AndRejectsHostileFrames()
        {
            var snap = new Snapshot { WorldSecond = 123, Weather = 2, TemperatureC = 30.5f, AckSequence = 7, YourPosition = new WorldPosition(1, 2, 3), Corrected = true };
            snap.Players.Add(new RemotePlayer { CharacterId = EntityId.Create(EntityKind.Character, 9), Name = "Ïñès", Position = new WorldPosition(4, 5, 6), WantedLevel = 3 });
            var back = (Snapshot)Wire.ReadFrame(new MemoryStream(Wire.Frame(snap)));
            Assert.AreEqual(123, back.WorldSecond);
            Assert.AreEqual("Ïñès", back.Players[0].Name);
            Assert.AreEqual(3, back.Players[0].WantedLevel);
            var req = new Request { RequestId = 5, Op = "property.buy", Key = "k", Args = { ["property"] = "Property:1" } };
            Assert.AreEqual("Property:1", ((Request)Wire.ReadFrame(new MemoryStream(Wire.Frame(req)))).Args["property"]);

            var ok = Wire.Frame(new Ping { Nonce = 1 });
            Assert.Throws<ProtocolException>(() => Wire.ReadFrame(new MemoryStream(ok.Take(ok.Length - 3).ToArray())), "truncated");
            var huge = BitConverter.GetBytes(Wire.MaxFrameBytes + 1).Concat(new byte[8]).ToArray();
            Assert.Throws<ProtocolException>(() => Wire.ReadFrame(new MemoryStream(huge)), "oversized length is refused before allocating");
            var unknown = BitConverter.GetBytes(2).Concat(BitConverter.GetBytes((ushort)999)).ToArray();
            Assert.Throws<ProtocolException>(() => Wire.ReadFrame(new MemoryStream(unknown)));
            var t = ok.Concat(new byte[] { 0 }).ToArray();
            BitConverter.GetBytes(ok.Length - 4 + 1).CopyTo(t, 0);
            Assert.Throws<ProtocolException>(() => Wire.ReadFrame(new MemoryStream(t)), "trailing bytes");
            var nan = new PacketWriter();
            nan.Int(1);
            nan.Float(float.NaN);
            Assert.Throws<ProtocolException>(() => new PacketReader(nan.ToArray()).Position());
            Assert.IsNull(Wire.ReadFrame(new MemoryStream(new byte[0])), "clean end of stream");
        }

        [Test]
        public void Tickets_AreValidOnlyForTheirServer_UntilExpiry_AndCannotBeForged()
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var key = TicketCodec.DeriveServerKey(MasterSecret, ServerId);
            var text = TicketFor("acc-1", "Mo Reyes");
            Assert.IsTrue(TicketCodec.TryVerify(key, text, ServerId, now, out var ticket, out _));
            Assert.AreEqual("Mo Reyes", ticket.DisplayName);
            Assert.IsFalse(TicketCodec.TryVerify(key, text, "other-server", now, out _, out _), "wrong audience");
            Assert.IsFalse(TicketCodec.TryVerify(TicketCodec.DeriveServerKey(MasterSecret, "other-server"), text, ServerId, now, out _, out _), "another server's key");
            Assert.IsFalse(TicketCodec.TryVerify(key, text, ServerId, now + 301, out _, out var expired));
            StringAssert.Contains("expired", expired);
            var forged = TicketCodec.Issue(TicketCodec.DeriveServerKey(MasterSecret, "evil-server"),
                new Ticket { AccountId = "admin", DisplayName = "x", Audience = ServerId, ExpiresUnix = now + 60, Nonce = "n" });
            Assert.IsFalse(TicketCodec.TryVerify(key, forged, ServerId, now, out _, out _), "a community server's key cannot mint tickets for us");
            var tampered = text.Replace(text.Substring(2, 4), "AAAA");
            Assert.IsFalse(TicketCodec.TryVerify(key, tampered, ServerId, now, out _, out _));
        }

        // ------------------------------------------------------------------ server

        [Test]
        public void TwoPlayers_JoinSeeEachOther_AndChatLocally()
        {
            var a = Join("acc-a", "Ava Stone");
            var b = Join("acc-b", "Ben Ortiz");
            Assert.AreEqual(ClientState.Connected, a.State);
            Assert.AreEqual("Test City", a.Welcome.ServerName);
            Assert.AreEqual(2, _world.Characters.Count);
            Assert.AreEqual(GameServer.AccountEntity("acc-a"), _world.Characters[a.Welcome.CharacterId].AccountId);

            Until(() => a.LastSnapshot != null && a.LastSnapshot.Players.Any(p => p.Name == "Ben Ortiz"));
            var heard = new List<string>();
            b.ChatReceived += m => heard.Add(m.FromName + ": " + m.Text);
            a.Chat(ChatChannel.Local, "  evening  ");
            Until(() => heard.Count > 0);
            Assert.AreEqual("Ava Stone: evening", heard[0]);

            // Rejoining the same account replaces the old session and keeps the same character.
            var again = Join("acc-a", "Ava Stone");
            Until(() => a.State == ClientState.Disconnected);
            Assert.AreEqual(a.Welcome.CharacterId, again.Welcome.CharacterId);
            StringAssert.Contains("another location", a.DisconnectReason);
        }

        [Test]
        public void BadTickets_Bans_Replays_AndVersionMismatches_AreRejected()
        {
            var bad = Join("x", "X", ticket: "garbage.ticket");
            Until(() => bad.State == ClientState.Disconnected);
            Assert.IsNotEmpty(bad.RejectReason);

            var reused = TicketFor("acc-r", "Rae");
            Assert.AreEqual(ClientState.Connected, Join("acc-r", "Rae", reused).State);
            var replay = Join("acc-r", "Rae", reused);
            Until(() => replay.State == ClientState.Disconnected);
            StringAssert.Contains("already used", replay.RejectReason);

            _moderation.AssignRole("", "owner-acc", "owner", bootstrap: true);
            Assert.IsTrue(_moderation.Perform(new ModerationAction { Kind = ModerationActionKind.Ban, ActorAccountId = "owner-acc", TargetAccountId = "acc-banned" }));
            var banned = Join("acc-banned", "Nope");
            Until(() => banned.State == ClientState.Disconnected);
            StringAssert.Contains("banned", banned.RejectReason);

            var old = new GameClient();
            _clients.Add(old);
            old.ConnectAsync("127.0.0.1", _server.Port, "", sendHello: false).Wait(3000);
            old.Send(new Hello { ProtocolVersion = 999, Ticket = TicketFor("acc-v", "V") });
            Until(() => old.State == ClientState.Disconnected || old.RejectReason.Length > 0);
            StringAssert.Contains("Version", old.RejectReason);
        }

        [Test]
        public void SilentConnections_AreDroppedAfterTheHandshakeTimeout()
        {
            var tcp = new System.Net.Sockets.TcpClient();
            tcp.Connect("127.0.0.1", _server.Port);
            Until(() => _server.ConnectionCount == 1);
            Until(() => _server.ConnectionCount == 0, 5000);
            tcp.Close();
        }

        [Test]
        public void Movement_IsValidated_TeleportsAreCorrected()
        {
            var a = Join("acc-m", "Mia");
            var conn = _server.Players.Single();
            var start = conn.Position;
            Thread.Sleep(300);
            a.SendState(new WorldPosition(start.X + 2f, start.Y, start.Z), 0f, 3f);
            Until(() => WorldPosition.DistanceXZ(conn.Position, start) > 1f);
            var legal = conn.Position;
            a.SendState(new WorldPosition(legal.X + 900f, legal.Y, legal.Z), 0f, 3f);
            Until(() => a.LastSnapshot != null && a.LastSnapshot.Corrected);
            Assert.AreEqual(legal.X, conn.Position.X, 0.01f, "teleport refused");
            Assert.AreEqual(legal.X, a.LastSnapshot.YourPosition.X, 0.01f, "client told where it really is");
            Assert.AreEqual(legal.X, _world.Characters[a.Welcome.CharacterId].LastPosition.X, 0.01f);
            Assert.GreaterOrEqual(conn.Violations, 1);
        }

        [Test]
        public void Floods_AndMalformedFrames_CloseOnlyTheOffender()
        {
            var good = Join("acc-g", "Good");
            var flooder = Join("acc-f", "Flood");
            for (var i = 0; i < 500; i++) flooder.Send(new Ping { Nonce = i });
            Until(() => flooder.State == ClientState.Disconnected, 5000);

            var broken = Join("acc-x", "Broken");
            broken.SendRaw(BitConverter.GetBytes(int.MaxValue));
            Until(() => broken.State == ClientState.Disconnected);

            Assert.AreEqual(ClientState.Connected, good.State);
            Assert.IsTrue(Call(good, "me.status").Success, "the server is still serving everyone else");
        }

        [Test]
        public void Requests_RunAsTheConnectionsCharacter_AndRetriesAreIdempotent()
        {
            var a = Join("acc-buyer", "Buyer");
            var me = _world.Characters[a.Welcome.CharacterId];
            var land = _world.Properties.All.Where(p => p.ForSale).OrderBy(p => p.ListingPriceCents).First();
            _world.AdminGrant(me.CheckingAccount, new Money(land.ListingPriceCents * 2), "test", "funds");
            var args = new Dictionary<string, string> { ["property"] = land.Id.ToString() };
            var r1 = Call(a, "property.buy", args, key: "buy-1");
            Assert.IsTrue(r1.Success, r1.Error);
            Assert.AreEqual(me.CharacterId, _world.Ownership.OwnerOf(land.Id));
            var r2 = Call(a, "property.buy", args, key: "buy-1");
            Assert.IsFalse(r2.Success, "same key → not applied twice");

            // Another player can't use the same client key to interfere (keys are scoped per account).
            var b = Join("acc-other", "Other");
            Assert.IsFalse(Call(b, "property.buy", args, key: "buy-1").Error.Contains("Duplicate"));

            Assert.IsFalse(Call(a, "no.such.op").Success);
            var badArgs = Call(a, "property.buy", new Dictionary<string, string> { ["property"] = "not-an-id" });
            Assert.IsFalse(badArgs.Success);
            StringAssert.Contains("not an id", badArgs.Error);
            var status = Call(a, "me.status");
            Assert.AreEqual(me.CharacterId.ToString(), status.Data["character"]);
        }

        [Test]
        public void CrimeRequests_RequireBeingThere_AndUseServerSideDisguise()
        {
            var a = Join("acc-c", "Crook");
            var store = _world.Businesses.Values.OrderBy(x => x.Id).First(x => x.TemplateId == "corner_store");
            var far = Call(a, "crime.rob", new Dictionary<string, string> { ["business"] = store.Id.ToString() });
            Assert.IsFalse(far.Success);
            StringAssert.Contains("not there", far.Error);
            Assert.IsFalse(Call(a, "character.mask", new Dictionary<string, string> { ["on"] = "true" }).Success, "no mask in inventory");

            // A client cannot pickpocket someone across town by naming them.
            var conn = _server.Players.Single();
            Core.Population.NpcRecord distant = null;
            foreach (var npc in _world.Population.Ordered)
                if (_world.Director.TryGetPosition(_world.Schedules.Resolve(npc, _world.Clock.Now), out var pos) && Core.Foundation.WorldPosition.DistanceXZ(pos, conn.Position) > 200f) { distant = npc; break; }
            Assert.IsNotNull(distant);
            var pick = Call(a, "crime.pickpocket", new Dictionary<string, string> { ["npc"] = distant.Id.ToString() });
            Assert.IsFalse(pick.Success);
            StringAssert.Contains("not within reach", pick.Error);
            Assert.AreEqual(0, _world.Characters[a.Welcome.CharacterId].Record.Charges.Count, "nothing happened");
        }

        [Test]
        public void Moderation_OnlyPermittedAccountsCanKick_AndActionsAreLogged()
        {
            _moderation.AssignRole("", "acc-admin", "admin", bootstrap: true);
            var admin = Join("acc-admin", "Admin");
            var player = Join("acc-p", "Player");
            var denied = Call(player, "admin.kick", new Dictionary<string, string> { ["account"] = "acc-admin" });
            Assert.IsFalse(denied.Success);
            var kicked = Call(admin, "admin.kick", new Dictionary<string, string> { ["account"] = "acc-p", ["reason"] = "testing" });
            Assert.IsTrue(kicked.Success, kicked.Error);
            Until(() => player.State == ClientState.Disconnected);
            StringAssert.Contains("testing", player.DisconnectReason);
            Assert.IsTrue(_moderation.History.Any(h => h.Kind == ModerationActionKind.Kick && h.TargetAccountId == "acc-p"));
            var grant = Call(admin, "admin.grant", new Dictionary<string, string> { ["amount"] = "50000", ["reason"] = "event prize" });
            Assert.IsTrue(grant.Success, grant.Error);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void PowerUse_OverTheWire_FiresFromTheServersPosition_AndTeleportsMoveIt()
        {
            var a = Join("acc-pw", "Blink");
            var me = _world.Characters[a.Welcome.CharacterId];
            var blink = new Core.Powers.PowerInstance { Stage = Core.Powers.PowerStage.Mastered, Progress = new Core.Powers.PowerProgress { Experience = 700f, PrecisionBonus = 0.4f } };
            blink.Definition.Components.Add(new Core.Powers.PowerComponent { Domain = Core.Powers.PowerDomain.Space, Verb = Core.Powers.EffectVerb.Traverse, Range = 60f, Magnitude = 0.8f, Precision = 0.9f, Efficiency = 0.8f });
            me.Powers.Powers.Add(blink);
            var conn = _server.Players.Single();
            var start = conn.Position;
            Response r = null;
            for (var i = 0; i < 20; i++)
            {
                me.Powers.Stamina = 1f;
                me.Powers.Strain = 0f;
                blink.CooldownUntilSecond = 0;
                _world.Clock.AdvanceGame(60);
                r = Call(a, "power.use", new Dictionary<string, string>
                {
                    ["power"] = "0", ["intensity"] = "1", ["target"] = "Point",
                    ["x"] = (start.X + 25f).ToString(System.Globalization.CultureInfo.InvariantCulture), ["z"] = start.Z.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                Assert.IsTrue(r.Success, r.Error);
                if (r.Data["success"] == "true") break;
            }
            Assert.AreEqual("Teleport", r.Data["effect"]);
            Assert.AreEqual(start.X + 25f, conn.Position.X, 0.01f, "server-side position moved");
            Until(() => a.LastSnapshot != null && a.LastSnapshot.Corrected && System.Math.Abs(a.LastSnapshot.YourPosition.X - (start.X + 25f)) < 0.1f);
            Assert.IsFalse(Call(a, "power.use", new Dictionary<string, string> { ["power"] = "5", ["intensity"] = "1" }).Success, "no such power");
            Assert.IsFalse(Call(a, "power.use", new Dictionary<string, string> { ["power"] = "0", ["intensity"] = "7" }).Success, "intensity is bounded");
        }

        [Test]
        public void RippleAndCivicRequests_PostReadAndRegister_AndMutedPlayersCannotPost()
        {
            _moderation.AssignRole("", "acc-admin2", "admin", bootstrap: true);
            var admin = Join("acc-admin2", "Admin");
            var a = Join("acc-r", "Rae");
            var posted = Call(a, "ripple.post", new Dictionary<string, string> { ["text"] = "First night in the city #hello" });
            Assert.IsTrue(posted.Success, posted.Error);
            var feed = Call(a, "ripple.feed", new Dictionary<string, string> { ["count"] = "5" });
            Assert.IsTrue(feed.Success, feed.Error);
            StringAssert.Contains("|Rae|", feed.Data["post0"], "the display name comes from the signed ticket");
            Assert.IsTrue(Call(admin, "ripple.like", new Dictionary<string, string> { ["post"] = posted.Data["id"] }).Success);

            Assert.IsFalse(Call(a, "civic.register_powers").Success, "nothing to register");
            Assert.IsFalse(Call(a, "civic.budget", new Dictionary<string, string> { ["Police"] = "1", ["rate"] = "0.9" }).Success, "not the mayor");
            Assert.IsFalse(Call(a, "civic.file", new Dictionary<string, string> { ["office"] = "Mayor" }).Success, "player elections are off by default");

            var radio = Call(a, "radio.now", new Dictionary<string, string> { ["station"] = "bayou_gold" });
            Assert.IsTrue(radio.Success, radio.Error);
            Assert.IsFalse(string.IsNullOrEmpty(radio.Data["title"]));
            Assert.IsFalse(Call(a, "radio.now", new Dictionary<string, string> { ["station"] = "nope" }).Success);

            Assert.IsTrue(Call(admin, "admin.mute", new Dictionary<string, string> { ["account"] = "acc-r", ["reason"] = "spam", ["minutes"] = "10" }).Success);
            var muted = Call(a, "ripple.post", new Dictionary<string, string> { ["text"] = "still here" });
            Assert.IsFalse(muted.Success);
            StringAssert.Contains("muted", muted.Error);
        }

        [Test]
        public void AHandlerThatThrows_FailsOnlyThatRequest_AndTheServerCarriesOn()
        {
            string logged = null;
            _server.Log += line => logged = line;
            _server.Router.Register("test.boom", ctx => throw new System.InvalidOperationException("secret internals"));
            var a = Join("acc-boom", "Boom");
            var r = Call(a, "test.boom");
            Assert.IsFalse(r.Success);
            Assert.AreEqual("Server error.", r.Error, "internal details are logged, not sent to the client");
            StringAssert.Contains("secret internals", logged);
            Assert.AreEqual(ClientState.Connected, a.State);
            Assert.IsTrue(Call(a, "me.status").Success, "the same connection keeps working");
            Assert.IsFalse(Call(a, "no.such.op").Success);
        }

        [Test]
        public void DisconnectMidPurchase_ThenRetry_ChargesOnce()
        {
            var a = Join("acc-dc", "Dee");
            var me = _world.Characters[a.Welcome.CharacterId];
            Assert.IsTrue(_world.AdminGrant(me.CheckingAccount, Money.FromDollars(500000L), "admin", "test").Success);
            var land = _world.Properties.All.Where(p => p.ForSale && p.ListingPriceCents > 0).OrderBy(p => p.ListingPriceCents).First();
            var before = _world.Ledger.BalanceOf(me.CheckingAccount);
            var pending = a.Request("property.buy", new Dictionary<string, string> { ["property"] = land.Id.ToString() }, "buy-once");
            a.Dispose(); // the player's connection dies right after sending
            Until(() => _server.Players.Count == 0);
            var after = _world.Ledger.BalanceOf(me.CheckingAccount);

            var b = Join("acc-dc", "Dee");
            var retry = Call(b, "property.buy", new Dictionary<string, string> { ["property"] = land.Id.ToString() }, "buy-once");
            if (_world.Ownership.IsOwnedBy(land.Id, me.CharacterId))
            {
                Assert.IsFalse(retry.Success, "the retry of a purchase that went through is refused");
                Assert.AreEqual(after, _world.Ledger.BalanceOf(me.CheckingAccount), "charged exactly once");
                Assert.Less(after.Cents, before.Cents);
            }
            else
            {
                Assert.IsTrue(retry.Success, "the request never arrived, so the retry buys it");
                Assert.IsTrue(_world.Ownership.IsOwnedBy(land.Id, me.CharacterId));
            }
            Assert.IsFalse(Call(b, "property.buy", new Dictionary<string, string> { ["property"] = land.Id.ToString() }, "buy-again").Success, "never twice");
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void DisconnectingDuringAChase_IsEvading_AndTheManhuntContinues()
        {
            var a = Join("acc-run", "Runner");
            var me = _world.Characters[a.Welcome.CharacterId];
            var incident = new Core.Crime.CrimeIncident { Id = _world.Ids.Next(EntityKind.CrimeIncident), CrimeTypeId = "store_robbery", Perpetrator = me.CharacterId, OccurredAt = _world.Clock.Now };
            _world.Justice.Incidents.Add(incident);
            _world.Wanted.ReportCrime(incident, _world.Content.FindCrime("store_robbery"), _world.Clock.Now, policeWitnessed: true);
            a.Dispose();
            Until(() => _server.Players.Count == 0);
            Assert.IsTrue(_world.Justice.Incidents.Any(i => i.Perpetrator == me.CharacterId && i.CrimeTypeId == "evading_police"), "logging off mid-chase is fleeing");
            Assert.IsNotNull(_world.Wanted.Get(me.CharacterId), "the police are still looking");
        }

        [Test]
        public void ServerShutdown_TellsEveryoneWhy()
        {
            var a = Join("acc-s1", "One");
            var b = Join("acc-s2", "Two");
            _server.Stop();
            Until(() => a.State == ClientState.Disconnected && b.State == ClientState.Disconnected);
            StringAssert.Contains("shutting down", a.DisconnectReason);
            StringAssert.Contains("shutting down", b.DisconnectReason);
        }

        private static System.Security.Cryptography.X509Certificates.X509Certificate2 TestCertificate()
        {
            try
            {
                using (var rsa = System.Security.Cryptography.RSA.Create(2048))
                {
                    var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=herogame-test", rsa,
                        System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                    using (var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)))
                        return new System.Security.Cryptography.X509Certificates.X509Certificate2(cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
                }
            }
            catch (PlatformNotSupportedException)
            {
                Assert.Ignore("Certificate generation is not supported on this runtime.");
                return null;
            }
        }

        [Test]
        public void Tls_PinnedConnectionsWork_WrongPinsAndPlaintextAreRefused()
        {
            var cert = TestCertificate();
            var world = WorldGenerator.Create("net-tls", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
            using (var secure = new GameServer(new GameServerOptions
            {
                ServerId = ServerId, TicketKey = TicketCodec.DeriveServerKey(MasterSecret, ServerId), HandshakeTimeoutMs = 1500, Certificate = cert,
            }, world, new ModerationService()))
            {
                secure.Start();
                void Pump(Func<bool> done)
                {
                    var start = Environment.TickCount;
                    while (!done() && Environment.TickCount - start < 4000)
                    {
                        secure.Pump();
                        foreach (var c in _clients) c.Pump();
                        Thread.Sleep(2);
                    }
                }

                var pin = Networking.Security.TlsPinning.Fingerprint(cert);
                Assert.AreEqual(64, pin.Length);
                var good = new GameClient();
                _clients.Add(good);
                good.ConnectAsync("127.0.0.1", secure.Port, TicketFor("acc-tls", "Tess"), tls: new Networking.Security.ClientTls { PinnedFingerprint = pin }).Wait(4000);
                Pump(() => good.State == ClientState.Connected);
                Assert.AreEqual(ClientState.Connected, good.State, good.DisconnectReason);
                Assert.IsTrue(good.Encrypted);
                Assert.IsTrue(secure.Players.Single().Encrypted);

                var wrongPin = new GameClient();
                _clients.Add(wrongPin);
                var bad = new string('0', 64);
                Assert.Catch<Exception>(() => wrongPin.ConnectAsync("127.0.0.1", secure.Port, TicketFor("acc-tls2", "Mal"), tls: new Networking.Security.ClientTls { PinnedFingerprint = bad }).Wait(4000),
                    "a man-in-the-middle certificate is rejected");
                Assert.AreNotEqual(ClientState.Connected, wrongPin.State);

                var plain = new GameClient();
                _clients.Add(plain);
                plain.ConnectAsync("127.0.0.1", secure.Port, TicketFor("acc-tls3", "Pat")).Wait(4000);
                Pump(() => plain.State == ClientState.Disconnected);
                Assert.AreNotEqual(ClientState.Connected, plain.State, "a secure server never talks plaintext");
                Assert.AreEqual(1, secure.Players.Count);
            }
        }

        [Test]
        public void PropImpacts_NeedAVehicle_AndBeingThere()
        {
            var a = Join("acc-prop", "Pat");
            var conn = _server.Players.Single();
            var near = _world.Destructibles.All.OrderBy(p => Core.Foundation.WorldPosition.DistanceXZ(p.Position, conn.Position)).First();
            var far = _world.Destructibles.All.OrderByDescending(p => Core.Foundation.WorldPosition.DistanceXZ(p.Position, conn.Position)).First();
            StringAssert.Contains("not there", Call(a, "prop.impact", new Dictionary<string, string> { ["prop"] = far.Id.ToString() }).Error);
            conn.Position = near.Position;
            StringAssert.Contains("vehicles", Call(a, "prop.impact", new Dictionary<string, string> { ["prop"] = near.Id.ToString() }).Error);
            Assert.AreEqual(Core.World.PropState.Intact, near.State, "a player on foot cannot claim to have flattened anything");
        }

        [Test]
        public void WorldChanges_ReachEveryone_LateJoinersGetTheFullState_AndRebuiltBuildingsCanBeFetched()
        {
            var a = Join("acc-rep-a", "Ana");
            Until(() => a.World.Ready);
            var me = _server.Players.Single();

            // Street furniture knocked down, a fire, a building bought by a player and one put up for sale.
            var prop = _world.Destructibles.All.First(p => p.Kind == "street_light");
            _world.Destructibles.Damage(prop, 10f, "test");
            var fire = _world.Dispatch.Report(Core.Emergency.EmergencyKind.Fire, prop.Position, 1, "test", severity: 0.6f);
            var home = _world.Properties.All.OrderBy(p => p.Id).First(p => p.ForSale && p.Kind != Core.Property.PropertyKind.Land);
            Assert.IsTrue(_world.AdminGrant(me.Character.CheckingAccount, new Money(home.ListingPriceCents + 10000000), "admin", "test funds").Success);
            var bought = Call(a, "property.buy", new Dictionary<string, string> { ["property"] = home.Id.ToString() });
            Assert.IsTrue(bought.Success, bought.Error);
            var listed = _world.Properties.All.OrderBy(p => p.Id).First(p => !p.ForSale && p.Id != home.Id && !_world.Characters.ContainsKey(_world.Ownership.OwnerOf(p.Id)));
            listed.ForSale = true;
            listed.ListingPriceCents = 25000000;

            Until(() => a.World.PropState(prop.Id) == (byte)Core.World.PropState.Destroyed && a.World.Fires.Any(f => f.Incident == fire.Id) &&
                        a.World.Property(home.Id).PlayerOwner == me.Character.CharacterId && a.World.Property(listed.Id).ForSale);
            Assert.IsFalse(a.World.Property(home.Id).ForSale, "sold: the sign comes down");
            Assert.AreEqual(25000000, a.World.Property(listed.Id).ListingPriceCents);

            // A rebuild bumps the layout version; the client fetches the new layout on demand.
            var rebuilt = home.Layout.Clone();
            rebuilt.Furniture.Add(new Core.Building.PlacedFurniture { Id = rebuilt.NextId++, CatalogId = "structural_column", X = 4f, Z = 4f });
            home.Layout = rebuilt;
            Until(() => a.World.NeedsLayout(home.Id));
            Assert.IsNull(a.World.Layout(home.Id));
            var fetched = Call(a, "property.layout", new Dictionary<string, string> { ["property"] = home.Id.ToString() });
            Assert.IsTrue(fetched.Success, fetched.Error);
            Until(() => a.World.Layout(home.Id) != null);
            Assert.AreEqual(rebuilt.Furniture.Count, a.World.Layout(home.Id).Furniture.Count);
            Assert.IsFalse(a.World.NeedsLayout(home.Id));

            // Someone arriving later gets all of it at once.
            var b = Join("acc-rep-b", "Ben");
            Until(() => b.World.Ready);
            Assert.AreEqual((byte)Core.World.PropState.Destroyed, b.World.PropState(prop.Id));
            Assert.IsTrue(b.World.Fires.Any(f => f.Incident == fire.Id));
            Assert.AreEqual(me.Character.CharacterId, b.World.Property(home.Id).PlayerOwner);
            Assert.AreEqual(1, b.World.Property(home.Id).LayoutVersion);
            Assert.IsTrue(b.World.NeedsLayout(home.Id));

            // Things going back to normal are replicated too: the fire is put out, the listing withdrawn, the light repaired.
            fire.FireIntensity = 0f;
            listed.ForSale = false;
            prop.State = Core.World.PropState.Intact;
            Until(() => a.World.FireCount == 0 && b.World.FireCount == 0 && !b.World.Property(listed.Id).ForSale && b.World.PropState(prop.Id) == 0);
            Assert.IsFalse(b.World.Properties.Any(p => p.Property == listed.Id), "defaults are not kept");

            // Quiet worlds cost nothing: with no changes, publishing produces an empty delta.
            _server.BroadcastWorldChanges();
            Assert.IsTrue(_server.Replicator.Publish().IsEmpty);
        }

        [Test]
        public void WorldDeltas_RoundTrip_AndHostileCountsAreRejected()
        {
            var d = new WorldDelta { Full = true };
            d.Props.Add(new PropChange { Id = 7, State = 2 });
            d.Fires.Add(new FireChange { Incident = new EntityId(99), Position = new WorldPosition(1, 0, 2), Intensity = 0.5f });
            d.Properties.Add(new PropertyChange { Property = new EntityId(5), ForSale = true, ListingPriceCents = 123, Damage = 3, LayoutVersion = 2 });
            var bytes = Wire.Frame(d);
            var back = (WorldDelta)Wire.ReadFrame(new MemoryStream(bytes));
            Assert.IsTrue(back.Full);
            Assert.AreEqual(7, back.Props[0].Id);
            Assert.AreEqual(0.5f, back.Fires[0].Intensity);
            Assert.AreEqual(123, back.Properties[0].ListingPriceCents);

            // A full-size split stays under the frame limit.
            var big = new WorldDelta();
            for (var i = 0; i < 5000; i++) big.Props.Add(new PropChange { Id = i, State = 1 });
            for (var i = 0; i < 3000; i++) big.Properties.Add(new PropertyChange { Property = new EntityId((ulong)i + 1), ForSale = true, LayoutVersion = 1 });
            for (var i = 0; i < 600; i++) big.Fires.Add(new FireChange { Incident = new EntityId((ulong)i + 1), Intensity = 1f });
            var parts = Networking.Server.WorldReplicator.Split(big);
            Assert.Greater(parts.Count, 2);
            foreach (var part in parts) Assert.LessOrEqual(Wire.Frame(part).Length, Wire.MaxFrameBytes + 4);
            Assert.AreEqual(5000, parts.Sum(p => p.Props.Count));

            var hostile = (byte[])bytes.Clone();
            // The prop count follows the length, the message type and the Full flag.
            BitConverter.GetBytes(int.MaxValue).CopyTo(hostile, 7);
            Assert.Throws<ProtocolException>(() => Wire.ReadFrame(new MemoryStream(hostile)));
            BitConverter.GetBytes(-1).CopyTo(hostile, 7);
            Assert.Throws<ProtocolException>(() => Wire.ReadFrame(new MemoryStream(hostile)));
        }

        [Test]
        public void Combat_OverTheWire_UsesTheServersPositions_AndRespectsPvp()
        {
            var a = Join("acc-fight-a", "Rae");
            var b = Join("acc-fight-b", "Sol");
            var ca = _server.Players.Single(p => p.AccountId == "acc-fight-a");
            var cb = _server.Players.Single(p => p.AccountId == "acc-fight-b");

            // An NPC across town is out of reach whatever the client claims.
            var npc = _world.Population.Ordered.First(n => n.Alive && n.AgeYears(_world.Today) >= 20 && _world.Director.TryGetPosition(_world.Schedules.Resolve(n, _world.Clock.Now), out _));
            _world.Director.TryGetPosition(_world.Schedules.Resolve(npc, _world.Clock.Now), out var npcAt);
            ca.Position = new WorldPosition(npcAt.X + 300f, 0f, npcAt.Z);
            var far = Call(a, "combat.attack", new Dictionary<string, string> { ["weapon"] = "fists", ["target"] = "npc", ["id"] = npc.Id.ToString() });
            Assert.IsFalse(far.Success);
            StringAssert.Contains("Too far", far.Error);

            ca.Position = npcAt;
            var near = Call(a, "combat.attack", new Dictionary<string, string> { ["weapon"] = "fists", ["target"] = "npc", ["id"] = npc.Id.ToString() });
            Assert.IsTrue(near.Success, near.Error);
            Assert.IsTrue(near.Data.ContainsKey("hit"));
            StringAssert.Contains("don't have", Call(a, "combat.attack", new Dictionary<string, string> { ["weapon"] = "compact_pistol", ["target"] = "none" }).Error, "no gun, no shot");

            // Player against player: only where the server allows it, and only within reach.
            cb.Position = ca.Position;
            _world.Clock.AdvanceGame(5);
            _world.Config.Gameplay.PvpEnabled = false;
            StringAssert.Contains("does not allow", Call(a, "combat.attack", new Dictionary<string, string> { ["weapon"] = "fists", ["target"] = "character", ["id"] = cb.Character.CharacterId.ToString() }).Error);
            _world.Config.Gameplay.PvpEnabled = true;
            Assert.IsTrue(Call(a, "combat.attack", new Dictionary<string, string> { ["weapon"] = "fists", ["target"] = "character", ["id"] = cb.Character.CharacterId.ToString() }).Success);

            // Buying needs the shop: from the street, no.
            var shop = _world.Businesses.Values.First(x => x.TemplateId == "hardware_store");
            StringAssert.Contains("not there", Call(b, "weapons.buy", new Dictionary<string, string> { ["business"] = shop.Id.ToString(), ["weapon"] = "crowbar" }).Error);
        }

        [Test]
        public void PlayerView_ArrivesOnJoin_FollowsTheServer_AndOnlyTheirOwn()
        {
            var a = Join("acc-view-a", "Uma");
            var b = Join("acc-view-b", "Vic");
            Until(() => a.Me != null && b.Me != null);
            var ca = _server.Players.Single(p => p.AccountId == "acc-view-a");
            var cash = a.Me.CashCents;

            Assert.IsTrue(_world.AdminGrant(ca.Character.CheckingAccount, Money.FromDollars(1234), "admin", "test").Success);
            _world.Phone.Send(ca.Character, EntityId.None, "Landlord", Core.Phone.MessageCategory.Personal, "Rent is due Friday.");
            Until(() => a.Me.CashCents == cash + 123400 && a.Me.Unread > 0, 4000);
            Assert.IsTrue(a.Me.Messages.Any(m => m.Body == "Rent is due Friday." && !m.Read));
            Assert.IsFalse(b.Me.Messages.Any(m => m.Body == "Rent is due Friday."), "nobody else sees it");

            Assert.IsTrue(Call(a, "phone.read").Success);
            Until(() => a.Me.Unread == 0, 4000);

            ca.Character.Inventory.Add(new InventoryStack { ItemId = "laptop", Quantity = 2 });
            Until(() => a.Me.Inventory.Any(i => i.Name == "Laptop computer" && i.Quantity == 2), 4000);

            // An unchanged view is not resent.
            var sent = ca.MessagesSent;
            _server.SendPlayerViews();
            _server.SendPlayerViews();
            Assert.LessOrEqual(ca.MessagesSent - sent, 0);
        }

        [Test]
        public void FinanceOverTheWire_QuotesCoverAndCancellation_ShowUpInThePlayerView()
        {
            var a = Join("acc-fin", "Wes");
            var me = _server.Players.Single(p => p.AccountId == "acc-fin").Character;
            Assert.IsTrue(_world.AdminGrant(me.CheckingAccount, Money.FromDollars(20000), "admin", "test").Success);
            var loanQuote = Call(a, "finance.quote", new Dictionary<string, string> { ["kind"] = "Personal", ["amount"] = "500000", ["term"] = "36" });
            Assert.IsTrue(loanQuote.Success, loanQuote.Error);
            Assert.IsTrue(loanQuote.Data.ContainsKey("approved") && loanQuote.Data.ContainsKey("monthly"));
            var cover = Call(a, "insurance.quote", new Dictionary<string, string> { ["kind"] = "Health", ["deductible"] = "100000" });
            Assert.IsTrue(cover.Success, cover.Error);
            Assert.AreEqual("true", cover.Data["available"], cover.Data["reason"]);
            Assert.IsTrue(Call(a, "insurance.buy", new Dictionary<string, string> { ["kind"] = "Health", ["deductible"] = "100000" }).Success);
            Until(() => a.Me != null && a.Me.Policies.Any(p => p.Kind == "Health" && p.Active), 4000);
            Assert.IsTrue(a.Me.Insurables.Any(i => i.Kind == "Health"));
            Assert.Greater(a.Me.CreditScore, 0);
            var policy = a.Me.Policies.First(p => p.Kind == "Health");
            Assert.IsTrue(Call(a, "insurance.cancel", new Dictionary<string, string> { ["policy"] = policy.Id }).Success);
            Until(() => a.Me.Policies.Any(p => p.Id == policy.Id && !p.Active), 4000);
        }

        [Test]
        public void StreetEncounters_ShowInThePlayerView_AndResolveOverTheWire()
        {
            var a = Join("acc-mug", "Zoe");
            var conn = _server.Players.Single(p => p.AccountId == "acc-mug");
            Assert.IsTrue(_world.AdminGrant(conn.Character.CheckingAccount, Money.FromDollars(900), "admin", "test").Success);
            var e = _world.StreetCrime.Start(conn.Character, conn.Position);
            Assert.IsNotNull(e);
            Until(() => a.Me != null && a.Me.Encounter != null, 4000);
            Assert.AreEqual(e.DemandCents, a.Me.Encounter.DemandCents);
            var cash = a.Me.CashCents;
            Assert.IsTrue(Call(a, "encounter.comply").Success);
            Until(() => a.Me.Encounter == null && a.Me.CashCents == cash - e.DemandCents, 4000);
            StringAssert.Contains("Nobody", Call(a, "encounter.refuse").Error);
        }

        [Test]
        public void AdminCommands_RequireWorldAdmin_AndAreAudited()
        {
            _server.Admin = new AdminCommands(_world, new WorldSimulation(_world));
            _moderation.AssignRole("", "acc-wa", "admin", bootstrap: true);
            var player = Join("acc-np", "Nobody");
            var denied = Call(player, "admin.cmd", new Dictionary<string, string> { ["line"] = "money 1000000" });
            Assert.IsFalse(denied.Success, "players cannot run world commands");
            var admin = Join("acc-wa", "Ada");
            var ok = Call(admin, "admin.cmd", new Dictionary<string, string> { ["line"] = "status" });
            Assert.IsTrue(ok.Success, ok.Error);
            StringAssert.Contains("Ledger balanced", ok.Data["output"]);
            Assert.IsTrue(_moderation.History.Any(h => h.Kind == ModerationActionKind.AdminCommand && h.ActorAccountId == "acc-wa" && h.Reason == "status"));
        }

        [Test]
        public void BuildOps_SurviveTheWireEncoding()
        {
            var op = new Core.Building.BuildOp { Kind = Core.Building.BuildOpKind.AddRoom, RoomType = Core.Building.RoomType.Bar, Floor = 1, Polygon = { 2, 2, 27.25f, 2, 27.25f, 47, 2, 47 } };
            var back = StandardRequests.ParseOp(StandardRequests.EncodeOp(op));
            Assert.AreEqual(op.Kind, back.Kind);
            Assert.AreEqual(op.RoomType, back.RoomType);
            CollectionAssert.AreEqual(op.Polygon, back.Polygon);
            Assert.Throws<ArgumentException>(() => StandardRequests.ParseOp("AddWall,0,0,1,1,1,1e30,Interior,0,,0"));
        }
    }
}
