using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
using NUnit.Framework;

namespace HeroGame.Tests
{
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
