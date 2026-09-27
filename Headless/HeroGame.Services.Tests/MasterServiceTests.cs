using System.Net;
using System.Net.Sockets;
using HeroGame.Core.Servers;
using HeroGame.Core.Simulation;
using HeroGame.Core.Economy;
using HeroGame.MasterServer;
using HeroGame.Networking.Auth;
using HeroGame.Networking.Client;
using HeroGame.Networking.Server;
using HeroGame.Services;
using NUnit.Framework;

namespace HeroGame.Services.Tests;

public class MasterServiceTests
{
    private static readonly byte[] Secret = Enumerable.Range(0, 48).Select(i => (byte)(i * 7)).ToArray();
    private long _now = 1_900_000_000;

    private AccountDirectory Accounts() => new(new JsonFileStore<AccountsDocument>(null), Secret, () => _now, iterations: 1000);
    private ServerDirectory Servers() => new(new JsonFileStore<ServersDocument>(null), Secret, () => _now);

    [Test]
    public void Accounts_ValidateInput_HashPasswords_AndLockAfterRepeatedFailures()
    {
        var accounts = Accounts();
        Assert.Throws<ServiceException>(() => accounts.Register("A!", "long-enough-pw", "Al"));
        Assert.Throws<ServiceException>(() => accounts.Register("alex", "short", "Alex"));
        Assert.Throws<ServiceException>(() => accounts.Register("alex", "long-enough-pw", "A|x"));
        var rec = accounts.Register("Alex_99", "long-enough-pw", "Alex Moreno");
        Assert.AreEqual("alex_99", rec.Username);
        Assert.IsFalse(rec.PasswordHash.Contains("long-enough-pw"));
        Assert.AreEqual(409, Assert.Throws<ServiceException>(() => accounts.Register("ALEX_99", "another-password", "Other"))!.Status);

        var (account, token, _) = accounts.Login("alex_99", "long-enough-pw");
        Assert.AreEqual(rec.AccountId, accounts.Authenticate(token).AccountId);
        Assert.AreEqual(account.AccountId, rec.AccountId);
        for (var i = 0; i < AccountDirectory.MaxFailedLogins; i++)
            Assert.AreEqual(401, Assert.Throws<ServiceException>(() => accounts.Login("alex_99", "wrong-password"))!.Status);
        Assert.AreEqual(429, Assert.Throws<ServiceException>(() => accounts.Login("alex_99", "long-enough-pw"))!.Status, "locked even with the right password");
        _now += AccountDirectory.LockoutSeconds + 1;
        Assert.DoesNotThrow(() => accounts.Login("alex_99", "long-enough-pw"));
        Assert.AreEqual(401, Assert.Throws<ServiceException>(() => accounts.Login("nobody", "whatever-pass"))!.Status);

        _now += AccountDirectory.SessionSeconds + 1;
        Assert.AreEqual(401, Assert.Throws<ServiceException>(() => accounts.Authenticate(token))!.Status, "sessions expire");
    }

    [Test]
    public void Sessions_CanBeSignedOut_Individually_Everywhere_AndByPasswordChange()
    {
        var accounts = Accounts();
        accounts.Register("sam", "long-enough-pw", "Sam");
        var (_, phone, _) = accounts.Login("sam", "long-enough-pw");
        _now += 5;
        var (_, laptop, _) = accounts.Login("sam", "long-enough-pw");
        accounts.Logout(phone);
        Assert.AreEqual(401, Assert.Throws<ServiceException>(() => accounts.Authenticate(phone))!.Status, "signed out now, not in 12 hours");
        Assert.DoesNotThrow(() => accounts.Authenticate(laptop), "other sessions are unaffected");

        _now += 5;
        var (_, tablet, _) = accounts.Login("sam", "long-enough-pw");
        _now += 5;
        accounts.LogoutEverywhere(laptop);
        Assert.Throws<ServiceException>(() => accounts.Authenticate(laptop));
        Assert.Throws<ServiceException>(() => accounts.Authenticate(tablet));
        _now += 1;
        var (_, fresh, _) = accounts.Login("sam", "long-enough-pw");
        Assert.DoesNotThrow(() => accounts.Authenticate(fresh), "signing in again works");

        Assert.AreEqual(401, Assert.Throws<ServiceException>(() => accounts.ChangePassword(fresh, "wrong-password", "new-long-password"))!.Status);
        accounts.ChangePassword(fresh, "long-enough-pw", "new-long-password");
        Assert.Throws<ServiceException>(() => accounts.Authenticate(fresh), "a password change signs everyone out");
        Assert.Throws<ServiceException>(() => accounts.Login("sam", "long-enough-pw"));
        _now += 1;
        Assert.DoesNotThrow(() => accounts.Authenticate(accounts.Login("sam", "new-long-password").token));
    }

    [Test]
    public void Heartbeat_PublishesTheServersTlsPin_AndRejectsGarbage()
    {
        var accounts = Accounts();
        var owner = accounts.Register("host", "long-enough-pw", "Host");
        var servers = Servers();
        var (record, key) = servers.Register(owner, new ServerListing { Name = "Pinned City", Host = "play.example", Port = 27015, MaxPopulation = 32 });
        var pin = new string('a', 64);
        servers.Heartbeat(record.Listing.ServerId, key, 3, 32, pin);
        Assert.AreEqual(pin, servers.Online().Single().TlsFingerprint);
        Assert.AreEqual(400, Assert.Throws<ServiceException>(() => servers.Heartbeat(record.Listing.ServerId, key, 3, 32, "not-hex"))!.Status);
        Assert.AreEqual(401, Assert.Throws<ServiceException>(() => servers.Heartbeat(record.Listing.ServerId, Convert.ToBase64String(new byte[32]), 3, 32, new string('b', 64)))!.Status,
            "only the server itself can change its pin");
        Assert.AreEqual(pin, servers.Online().Single().TlsFingerprint);
    }

    [Test]
    public void Servers_RegisterUnderAnAccount_HeartbeatWithTheirKey_AndDropOffWhenSilent()
    {
        var accounts = Accounts();
        var servers = Servers();
        var owner = accounts.Register("host", "long-enough-pw", "Host Person");
        var (record, key) = servers.Register(owner, new ServerListing { Name = "Harbor Nights", Host = "play.example.test", Port = 27015, MaxPopulation = 48 });
        Assert.AreEqual("Host Person", record.Listing.Owner);
        Assert.IsEmpty(servers.Online(), "not listed until it proves it's alive");
        Assert.AreEqual(401, Assert.Throws<ServiceException>(() => servers.Heartbeat(record.Listing.ServerId, Convert.ToBase64String(new byte[32]), 1, 48))!.Status);
        servers.Heartbeat(record.Listing.ServerId, key, 12, 48);
        Assert.AreEqual(12, servers.Online().Single().Population);

        var player = accounts.Register("player", "long-enough-pw", "Pat");
        var ticket = servers.IssueTicket(player, record.Listing.ServerId);
        Assert.IsTrue(TicketCodec.TryVerify(Convert.FromBase64String(key), ticket, record.Listing.ServerId, _now, out var t, out _));
        Assert.AreEqual("Pat", t.DisplayName);

        _now += ServerDirectory.OnlineWindowSeconds + 1;
        Assert.IsEmpty(servers.Online());
        Assert.AreEqual(409, Assert.Throws<ServiceException>(() => servers.IssueTicket(player, record.Listing.ServerId))!.Status);
        for (var i = 1; i < ServerDirectory.MaxServersPerAccount; i++) servers.Register(owner, new ServerListing { Name = "Extra " + i, Host = "h", Port = 1, MaxPopulation = 4 });
        Assert.AreEqual(403, Assert.Throws<ServiceException>(() => servers.Register(owner, new ServerListing { Name = "Too many", Host = "h", Port = 1, MaxPopulation = 4 }))!.Status);
    }

    [Test]
    public void Stores_PersistAtomically()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hg-master-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "accounts.json");
        new AccountDirectory(new JsonFileStore<AccountsDocument>(path), Secret, () => _now, 1000).Register("persist", "long-enough-pw", "Per Sist");
        Assert.IsFalse(File.Exists(path + ".tmp"));
        var reopened = new AccountDirectory(new JsonFileStore<AccountsDocument>(path), Secret, () => _now, 1000);
        Assert.AreEqual(1, reopened.Count);
        Assert.DoesNotThrow(() => reopened.Login("persist", "long-enough-pw"));
        Directory.Delete(dir, true);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Test]
    public async Task FullStack_LoginOverHttp_TicketFromMaster_JoinTheGameServer()
    {
        var url = "http://127.0.0.1:" + FreePort();
        var app = MasterApp.Build(new MasterOptions { Secret = Secret, Urls = url, PasswordIterations = 1000 });
        await app.StartAsync();
        try
        {
            using var hostClient = new MasterClient(url);
            await hostClient.Register("cityhost", "long-enough-pw", "City Host");
            await hostClient.Login("cityhost", "long-enough-pw");
            var registration = await hostClient.RegisterServer(new ServerListing { Name = "Port Arden Live", Host = "127.0.0.1", Port = 1, MaxPopulation = 32 });

            // The game server only ever receives its own derived key.
            var world = WorldGenerator.Create("fullstack", global::HeroGame.Tests.TestContent.DefaultConfig(), global::HeroGame.Tests.TestContent.Load(), new MemoryTransactionJournal());
            using var server = new GameServer(new GameServerOptions
            {
                ServerId = registration.ServerId, ServerName = "Port Arden Live", TicketKey = Convert.FromBase64String(registration.ServerKey),
            }, world, new ModerationService());
            server.Start();
            await hostClient.Heartbeat(registration.ServerId, registration.ServerKey, 0, 32);

            using var player = new MasterClient(url);
            await player.Register("walker", "long-enough-pw", "Walker Lane");
            var login = await player.Login("walker", "long-enough-pw");
            var list = await player.ListServers();
            Assert.AreEqual("Port Arden Live", list.Single().Name);
            Assert.AreEqual("City Host", list.Single().Owner);
            var join = await player.RequestTicket(registration.ServerId);

            using var game = new GameClient();
            await game.ConnectAsync("127.0.0.1", server.Port, join.Ticket);
            var deadline = Environment.TickCount + 3000;
            while (game.State != ClientState.Connected && Environment.TickCount < deadline)
            {
                server.Pump();
                game.Pump();
                await Task.Delay(5);
            }
            Assert.AreEqual(ClientState.Connected, game.State, game.RejectReason);
            Assert.AreEqual(GameServer.AccountEntity(login.AccountId), world.Characters[game.Welcome.CharacterId].AccountId);
            Assert.AreEqual(1, server.Players.Count);
            Assert.AreEqual(login.AccountId, server.Players[0].AccountId);

            var wrong = Assert.ThrowsAsync<MasterServerException>(() => player.RequestTicket("srv_nope"));
            Assert.AreEqual(404, wrong!.Status);
            player.Token = "forged";
            Assert.AreEqual(401, Assert.ThrowsAsync<MasterServerException>(() => player.RequestTicket(registration.ServerId))!.Status);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
