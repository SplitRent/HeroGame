using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Servers;
using HeroGame.Core.Simulation;
using HeroGame.Networking.Auth;
using HeroGame.Networking.Client;
using HeroGame.Networking.Server;
using HeroGame.Persistence.Content;

namespace HeroGame.Server
{

/// <summary>
/// herogame-server loadtest — runs a real server and N bot clients over loopback TCP (optionally TLS) in one
/// process and measures what the server's main thread spends per tick (message handling, simulation, snapshots),
/// bandwidth per client and request latency. Bots walk around the spawn so every snapshot carries the worst case:
/// everyone in view of everyone.
///
///   herogame-server loadtest [--clients 128] [--seconds 30] [--tick-hz 30] [--snapshot-hz 10] [--tls] [--data DIR]
/// </summary>
public static class LoadTest
{
    public static int Run(Dictionary<string, string> o, string dataDir)
    {
        var clients = o.TryGetValue("clients", out var c) ? int.Parse(c) : 128;
        var seconds = o.TryGetValue("seconds", out var s) ? double.Parse(s, System.Globalization.CultureInfo.InvariantCulture) : 30;
        var tickHz = o.TryGetValue("tick-hz", out var th) ? int.Parse(th) : 30;
        var snapshotHz = o.TryGetValue("snapshot-hz", out var sh) ? int.Parse(sh) : 10;
        var tls = o.ContainsKey("tls");

        var content = ContentLoader.Load(dataDir);
        var config = ContentLoader.LoadServerConfig(Path.Combine(dataDir, ContentLoader.DefaultServerConfig));
        config.Gameplay.MaxPlayers = Math.Max(config.Gameplay.MaxPlayers, clients);
        var world = WorldGenerator.Create("loadtest", config, content, new MemoryTransactionJournal());
        var sim = new WorldSimulation(world);
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var spawn = new WorldPosition(-200f, 0f, 40f);
        var options = new GameServerOptions
        {
            ServerId = "loadtest", TicketKey = TicketCodec.DeriveServerKey(secret, "loadtest"), Bind = IPAddress.Loopback, MaxPlayers = clients,
            Spawn = spawn, HandshakeTimeoutMs = 20000, MessagesPerSecond = 60, Burst = 120,
        };
        string pin = null;
        if (tls)
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=loadtest", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            options.Certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
            pin = Networking.Security.TlsPinning.Fingerprint(options.Certificate);
        }
        using var server = new GameServer(options, world, new ModerationService());
        var violations = 0;
        var samples = new System.Collections.Concurrent.ConcurrentQueue<string>();
        server.Log += line =>
        {
            if (!line.StartsWith("Violation", StringComparison.Ordinal)) return;
            if (Interlocked.Increment(ref violations) <= 5) samples.Enqueue(line);
        };
        server.Start();
        Console.WriteLine("Load test: " + clients + " clients, " + seconds + " s, " + tickHz + " Hz ticks, " + snapshotHz + " Hz snapshots, TLS " + (tls ? "on" : "off") +
                          ", " + world.Population.Count + " residents.");

        // Connect the bots (the server pumps meanwhile so handshakes complete).
        var bots = new List<GameClient>();
        var connect = new List<Task>();
        for (var i = 0; i < clients; i++)
        {
            var bot = new GameClient();
            bots.Add(bot);
            var ticket = TicketCodec.Issue(options.TicketKey, new Ticket
            {
                AccountId = "bot" + i, DisplayName = "Bot " + i, Audience = "loadtest", ExpiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600, Nonce = TicketCodec.NewNonce(),
            });
            connect.Add(bot.ConnectAsync("127.0.0.1", server.Port, ticket, tls: pin != null ? new Networking.Security.ClientTls { PinnedFingerprint = pin } : null));
        }
        var joinWatch = Stopwatch.StartNew();
        while (joinWatch.Elapsed.TotalSeconds < 60 && bots.Any(b => b.State != ClientState.Connected))
        {
            server.Pump();
            foreach (var b in bots) b.Pump();
            Thread.Sleep(5);
        }
        var joined = bots.Count(b => b.State == ClientState.Connected);
        Console.WriteLine("Joined " + joined + "/" + clients + " in " + joinWatch.ElapsedMilliseconds + " ms.");

        // Bots run on their own thread, like real clients on other machines.
        var stop = new CancellationTokenSource();
        long requestsSent = 0, requestsAnswered = 0, latencyTicks = 0;
        var botThread = new Thread(() =>
        {
            var rng = new Random(7);
            var angle = new double[bots.Count];
            var radius = new double[bots.Count];
            var start0 = new double[bots.Count];
            for (var i = 0; i < bots.Count; i++) { angle[i] = start0[i] = rng.NextDouble() * Math.PI * 2; radius[i] = 10 + rng.NextDouble() * 60; }
            var tick = 0;
            while (!stop.IsCancellationRequested)
            {
                var start = Stopwatch.GetTimestamp();
                for (var i = 0; i < bots.Count; i++)
                {
                    var b = bots[i];
                    b.Pump();
                    if (b.State != ClientState.Connected) continue;
                    // Each bot walks a circle that passes through the spawn point, where the server placed it.
                    var cx = spawn.X - Math.Cos(start0[i]) * radius[i];
                    var cz = spawn.Z - Math.Sin(start0[i]) * radius[i];
                    angle[i] += 3.0 * 0.1 / radius[i]; // walking pace, 3 m/s
                    var p = new WorldPosition((float)(cx + Math.Cos(angle[i]) * radius[i]), 0f, (float)(cz + Math.Sin(angle[i]) * radius[i]));
                    b.SendState(p, (float)angle[i], 3f);
                    if ((tick + i) % 50 == 0)
                    {
                        Interlocked.Increment(ref requestsSent);
                        var sent = Stopwatch.GetTimestamp();
                        b.Request("me.status").ContinueWith(t =>
                        {
                            if (t.IsCompletedSuccessfully && t.Result.Success)
                            {
                                Interlocked.Increment(ref requestsAnswered);
                                Interlocked.Add(ref latencyTicks, Stopwatch.GetTimestamp() - sent);
                            }
                        });
                    }
                    if ((tick + i * 7) % 300 == 0) b.Chat(Networking.Protocol.ChatChannel.Local, "hello from bot " + i);
                }
                tick++;
                var elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                Thread.Sleep(Math.Max(1, 100 - (int)elapsed));
            }
        }) { IsBackground = true, Name = "bots" };
        botThread.Start();

        // The server's main loop, measured.
        var tickMs = 1000.0 / tickHz;
        var snapshotEvery = Math.Max(1, tickHz / snapshotHz);
        var pumpTimes = new List<double>();
        var simTimes = new List<double>();
        var snapTimes = new List<double>();
        var totalTimes = new List<double>();
        var bytesBefore = server.Players.Sum(p => p.BytesSent);
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var run = Stopwatch.StartNew();
        var ticks = 0;
        while (run.Elapsed.TotalSeconds < seconds)
        {
            var t0 = Stopwatch.GetTimestamp();
            server.Pump();
            var t1 = Stopwatch.GetTimestamp();
            world.Clock.AdvanceReal(tickMs / 1000.0);
            sim.Update();
            var t2 = Stopwatch.GetTimestamp();
            if (ticks % snapshotEvery == 0) server.BroadcastSnapshots();
            var t3 = Stopwatch.GetTimestamp();
            double Ms(long a, long b) => (b - a) * 1000.0 / Stopwatch.Frequency;
            pumpTimes.Add(Ms(t0, t1));
            simTimes.Add(Ms(t1, t2));
            if (ticks % snapshotEvery == 0) snapTimes.Add(Ms(t2, t3));
            totalTimes.Add(Ms(t0, t3));
            ticks++;
            var spent = Ms(t0, Stopwatch.GetTimestamp());
            if (spent < tickMs) Thread.Sleep((int)(tickMs - spent));
        }
        var wall = run.Elapsed.TotalSeconds;
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalSeconds;
        stop.Cancel();
        botThread.Join(2000);
        var connected = server.Players.Count;
        var bytes = server.Players.Sum(p => p.BytesSent) - bytesBefore;

        static string P(List<double> v, double q)
        {
            if (v.Count == 0) return "-";
            var sorted = v.OrderBy(x => x).ToList();
            return sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))].ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }
        Console.WriteLine("Ticks: " + ticks + " in " + wall.ToString("0.0") + " s (" + (ticks / wall).ToString("0.0") + " Hz achieved)");
        Console.WriteLine("Main-thread ms per tick   p50 " + P(totalTimes, 0.5) + "  p95 " + P(totalTimes, 0.95) + "  p99 " + P(totalTimes, 0.99) + "  max " + P(totalTimes, 1.0) +
                          "  (budget " + tickMs.ToString("0.0") + ")");
        Console.WriteLine("  message handling        p50 " + P(pumpTimes, 0.5) + "  p99 " + P(pumpTimes, 0.99));
        Console.WriteLine("  simulation              p50 " + P(simTimes, 0.5) + "  p99 " + P(simTimes, 0.99) + "  max " + P(simTimes, 1.0));
        Console.WriteLine("  snapshots (all clients) p50 " + P(snapTimes, 0.5) + "  p99 " + P(snapTimes, 0.99));
        Console.WriteLine("Bandwidth: " + (bytes / wall / 1024.0).ToString("0") + " KiB/s total, " + (bytes / wall / Math.Max(1, connected) / 1024.0).ToString("0.0") + " KiB/s per client (server → clients)");
        Console.WriteLine("Requests: " + requestsAnswered + "/" + requestsSent + " answered, mean latency " +
                          (requestsAnswered > 0 ? (latencyTicks * 1000.0 / Stopwatch.Frequency / requestsAnswered).ToString("0.0") : "-") + " ms");
        Console.WriteLine("Still connected: " + connected + "/" + joined + ", violations: " + violations + ", process CPU " + (cpu / wall * 100).ToString("0") + "% of one core");
        foreach (var line in samples) Console.WriteLine("  e.g. " + line);
        foreach (var b in bots) b.Dispose();
        server.Stop();
        var ok = joined == clients && connected == joined && violations == 0;
        Console.WriteLine(ok ? "LOAD TEST PASSED" : "LOAD TEST FAILED");
        return ok ? 0 : 1;
    }
}
}
