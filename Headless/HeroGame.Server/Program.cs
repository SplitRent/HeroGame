using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using HeroGame.Core.Servers;
using HeroGame.Core.Simulation;
using HeroGame.Networking.Auth;
using HeroGame.Networking.Client;
using HeroGame.Networking.Server;
using HeroGame.Persistence.Content;
using HeroGame.Persistence.Json;
using HeroGame.Persistence.Saves;
using HeroGame.Persistence.Storage;

namespace HeroGame.Server
{
    /// <summary>
    /// herogame-server — dedicated game server.
    ///
    ///   herogame-server --save DIR [--data DIR] [--port 27015] [--bind 0.0.0.0] [--name "My City"] [--max 64]
    ///                   [--master URL --server-id ID --server-key BASE64 --public-host HOST]
    ///                   [--dev-secret BASE64]   (offline/LAN: verify tickets with a local secret; 'ticket' console command mints them)
    ///                   [--owner ACCOUNT_ID]    (grant the owner role to an account on first start)
    ///                   [--cert FILE.pfx [--cert-password PW] | --no-tls]
    ///                                           (TLS is on by default with a self-signed certificate kept in the save
    ///                                            directory; its fingerprint is published to the master for pinning)
    ///
    ///   herogame-server loadtest [--clients 128] [--seconds 30] [--tls]   (in-process bots; see LoadTest)
    ///
    /// Console: status | save | ticket ACCOUNT NAME | op ACCOUNT ROLE | kick ACCOUNT | say TEXT | stop
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "loadtest")
            {
                var lo = Args(args.Skip(1).ToArray());
                return LoadTest.Run(lo, lo.TryGetValue("data", out var ld) ? ld : FindDataDirectory());
            }
            var o = Args(args);
            if (!o.ContainsKey("save"))
            {
                Console.Error.WriteLine("usage: herogame-server --save DIR [--port N] [--name NAME] [--dev-secret B64 | --master URL --server-id ID --server-key B64]");
                return 2;
            }
            try
            {
                return Run(o);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("fatal: " + ex.Message);
                return 1;
            }
        }

        private static int Run(Dictionary<string, string> o)
        {
            var saveDir = o["save"];
            var dataDir = o.TryGetValue("data", out var d) ? d : Path.Combine(AppContext.BaseDirectory, "Data");
            if (!Directory.Exists(dataDir)) dataDir = FindDataDirectory();
            var content = ContentLoader.Load(dataDir);
            var report = ContentLoader.Validate(content);
            if (report.HasErrors) throw new InvalidDataException("Content invalid: " + report);

            var serverId = o.TryGetValue("server-id", out var sid) ? sid : "local";
            byte[] ticketKey;
            byte[] devSecret = null;
            if (o.TryGetValue("server-key", out var key)) ticketKey = Convert.FromBase64String(key);
            else if (o.TryGetValue("dev-secret", out var dev))
            {
                devSecret = Convert.FromBase64String(dev);
                ticketKey = TicketCodec.DeriveServerKey(devSecret, serverId);
            }
            else throw new ArgumentException("Provide --server-key (from the master) or --dev-secret (LAN/offline).");

            var saves = new WorldSaveSystem(saveDir);
            var journal = saves.OpenJournal();
            World world;
            if (saves.Exists)
            {
                var load = saves.Load(content, journal);
                if (load.Report.HasErrors) throw new InvalidDataException("Save failed integrity checks: " + load.Report);
                world = load.World;
                Console.WriteLine("Loaded world " + world.ServerId + " at " + world.Clock.Now + (load.JournalEntriesReplayed > 0 ? " (recovered " + load.JournalEntriesReplayed + " transactions)" : ""));
            }
            else
            {
                var config = ContentLoader.LoadServerConfig(Path.Combine(dataDir, ContentLoader.DefaultServerConfig));
                world = WorldGenerator.Create(serverId, config, content, journal);
                saves.Save(world, full: true);
                Console.WriteLine("Created world " + serverId + " with " + world.Population.Count + " residents.");
            }
            var sim = new WorldSimulation(world);
            sim.Update(); // offline catch-up since the last save

            var moderationPath = Path.Combine(saveDir, "moderation.json");
            var moderation = new ModerationService();
            if (File.Exists(moderationPath)) moderation.Import(JsonSetup.Deserialize<ModerationSnapshot>(AtomicFile.ReadAllText(moderationPath)));
            if (o.TryGetValue("owner", out var owner)) moderation.AssignRole("", owner, "owner", bootstrap: true);

            var options = new GameServerOptions
            {
                ServerId = serverId,
                ServerName = o.TryGetValue("name", out var name) ? name : world.Config.Identity.CityName,
                TicketKey = ticketKey,
                Bind = o.TryGetValue("bind", out var bind) ? IPAddress.Parse(bind) : IPAddress.Any,
                Port = o.TryGetValue("port", out var port) ? int.Parse(port) : 27015,
                MaxPlayers = o.TryGetValue("max", out var max) ? int.Parse(max) : world.Config.Gameplay.MaxPlayers,
                Spawn = new Core.Foundation.WorldPosition(-200f, 0f, 40f),
            };
            if (!o.ContainsKey("no-tls"))
                options.Certificate = o.TryGetValue("cert", out var certPath)
                    ? new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, o.TryGetValue("cert-password", out var pw) ? pw : null)
                    : SelfSignedCertificate(Path.Combine(saveDir, "server-tls.pfx"), serverId);
            var fingerprint = options.Certificate != null ? Networking.Security.TlsPinning.Fingerprint(options.Certificate) : null;
            Console.WriteLine(fingerprint != null ? "TLS on; certificate fingerprint " + fingerprint : "TLS OFF (--no-tls): game traffic is not encrypted.");
            using var server = new GameServer(options, world, moderation);
            server.Log += line => Console.WriteLine("[" + DateTime.UtcNow.ToString("HH:mm:ss") + "] " + line);
            server.Start();

            MasterClient master = null;
            if (o.TryGetValue("master", out var masterUrl)) master = new MasterClient(masterUrl);

            var commands = new ConcurrentQueue<string>();
            var input = new Thread(() =>
            {
                string line;
                while ((line = Console.ReadLine()) != null) commands.Enqueue(line.Trim());
            }) { IsBackground = true };
            input.Start();
            var stopping = false;
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                commands.Enqueue("stop");
            };

            var frame = Stopwatch.StartNew();
            var sinceSnapshot = 0.0;
            var sinceSave = 0.0;
            System.Threading.Tasks.Task<SaveResult> autosave = null;
            var sinceHeartbeat = 1e9;
            Console.WriteLine("Server '" + options.ServerName + "' running on port " + server.Port + ". Type 'help' for commands.");
            while (!stopping)
            {
                var dt = frame.Elapsed.TotalSeconds;
                frame.Restart();
                world.Clock.AdvanceReal((float)dt);
                sim.Update();
                server.Pump();
                sinceSnapshot += dt;
                if (sinceSnapshot >= 0.1)
                {
                    sinceSnapshot = 0;
                    server.BroadcastSnapshots();
                }
                sinceSave += dt;
                if (sinceSave >= 300 && autosave == null)
                {
                    sinceSave = 0;
                    // Serialize now (consistent snapshot), write on a background thread.
                    autosave = saves.SaveInBackground(world);
                    AtomicFile.WriteAllText(moderationPath, JsonSetup.Serialize(moderation.Export()));
                }
                if (autosave != null && autosave.IsCompleted)
                {
                    if (autosave.IsFaulted) Console.WriteLine("autosave failed (will retry): " + autosave.Exception?.GetBaseException().Message);
                    else saves.CompactJournal(world, journal); // the journal belongs to this thread
                    autosave = null;
                }
                sinceHeartbeat += dt;
                if (master != null && sinceHeartbeat >= 30 && o.TryGetValue("server-key", out var serverKey))
                {
                    sinceHeartbeat = 0;
                    var players = server.Players.Count;
                    master.Heartbeat(serverId, serverKey, players, options.MaxPlayers, fingerprint ?? "").ContinueWith(t =>
                    {
                        if (t.IsFaulted) Console.WriteLine("heartbeat failed: " + t.Exception?.GetBaseException().Message);
                    });
                }
                while (commands.TryDequeue(out var cmd)) stopping |= Command(cmd, server, world, saves, journal, moderation, moderationPath, devSecret, serverId);
                Thread.Sleep(15);
            }
            server.Stop();
            Save(saves, world, journal, moderation, moderationPath);
            journal.Dispose();
            Console.WriteLine("Saved and stopped.");
            return 0;
        }

        /// <summary>Loads the server's self-signed certificate, creating it (RSA-2048, 5 years) on first start.</summary>
        private static System.Security.Cryptography.X509Certificates.X509Certificate2 SelfSignedCertificate(string path, string serverId)
        {
            if (!File.Exists(path))
            {
                using var rsa = System.Security.Cryptography.RSA.Create(2048);
                var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=herogame-" + serverId, rsa,
                    System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
                File.WriteAllBytes(path, created.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            return new System.Security.Cryptography.X509Certificates.X509Certificate2(File.ReadAllBytes(path));
        }

        private static bool Command(string line, GameServer server, World world, WorldSaveSystem saves, FileTransactionJournal journal,
            ModerationService moderation, string moderationPath, byte[] devSecret, string serverId)
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;
            switch (parts[0])
            {
                case "help":
                    Console.WriteLine("status | save | ticket ACCOUNT NAME | op ACCOUNT ROLE | kick ACCOUNT | say TEXT | stop");
                    break;
                case "status":
                    Console.WriteLine(world.Clock.Now + " · " + server.Players.Count + " online · " + world.Population.Count + " residents · ledger "
                                      + (world.Ledger.VerifyInvariant(out _) ? "balanced" : "BROKEN"));
                    foreach (var p in server.Players) Console.WriteLine("  " + p.Ticket.DisplayName + " (" + p.AccountId + ") at " + p.Position);
                    break;
                case "save":
                    Save(saves, world, journal, moderation, moderationPath);
                    Console.WriteLine("Saved.");
                    break;
                case "ticket" when parts.Length == 3:
                    if (devSecret == null)
                    {
                        Console.WriteLine("Tickets come from the master server (start with --dev-secret for LAN play).");
                        break;
                    }
                    Console.WriteLine(TicketCodec.Issue(TicketCodec.DeriveServerKey(devSecret, serverId), new Ticket
                    {
                        AccountId = parts[1], DisplayName = parts[2], Audience = serverId, ExpiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600, Nonce = TicketCodec.NewNonce(),
                    }));
                    break;
                case "op" when parts.Length == 3:
                    Console.WriteLine(moderation.AssignRole("", parts[1], parts[2], bootstrap: true) ? "Role set." : "Unknown role.");
                    break;
                case "kick" when parts.Length >= 2:
                    var target = server.FindByAccount(parts[1]);
                    if (target != null) server.Kick(target, parts.Length == 3 ? parts[2] : "Kicked by the console.");
                    break;
                case "say" when parts.Length >= 2:
                    var text = line.Substring(4);
                    foreach (var p in server.Players) p.Send(new Networking.Protocol.ChatMessage { Channel = Networking.Protocol.ChatChannel.Server, FromName = "Server", Text = text });
                    break;
                case "stop":
                    return true;
                default:
                    Console.WriteLine("Unknown command. Type 'help'.");
                    break;
            }
            return false;
        }

        private static void Save(WorldSaveSystem saves, World world, FileTransactionJournal journal, ModerationService moderation, string moderationPath)
        {
            saves.Save(world);
            saves.CompactJournal(world, journal);
            AtomicFile.WriteAllText(moderationPath, JsonSetup.Serialize(moderation.Export()));
        }

        private static string FindDataDirectory()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Game", "Assets", "StreamingAssets", "Data");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Content data not found; pass --data DIR.");
        }

        private static Dictionary<string, string> Args(string[] args)
        {
            var map = new Dictionary<string, string>();
            for (var i = 0; i < args.Length; i++)
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                    map[args[i].Substring(2)] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            return map;
        }
    }
}
