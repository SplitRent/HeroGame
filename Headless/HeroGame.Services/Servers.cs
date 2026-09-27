using System.Security.Cryptography;
using HeroGame.Core.Servers;
using HeroGame.Networking.Auth;

namespace HeroGame.Services;

public sealed class ServerRecord
{
    public ServerListing Listing = new();
    public string OwnerAccountId = "";
    public long CreatedUnix;
    public long LastHeartbeatUnix;
}

public sealed class ServersDocument
{
    public List<ServerRecord> Servers = new();
}

/// <summary>
/// The public server list (GDD §4): community servers register under an account, prove liveness with
/// heartbeats signed by their own key, and drop off the list when they stop. Join tickets are signed with the
/// target server's derived key, so only that server accepts them.
/// </summary>
public sealed class ServerDirectory
{
    public const int MaxServersPerAccount = 5;
    public const long OnlineWindowSeconds = 90;
    public const long TicketSeconds = 60;

    private readonly JsonFileStore<ServersDocument> _store;
    private readonly byte[] _masterSecret;
    private readonly Func<long> _now;
    private readonly object _lock = new();

    public ServerDirectory(JsonFileStore<ServersDocument> store, byte[] masterSecret, Func<long>? now = null)
    {
        _store = store;
        _masterSecret = masterSecret;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public (ServerRecord record, string key) Register(AccountRecord owner, ServerListing listing)
    {
        if (listing == null) throw new ServiceException(400, "Missing server details.");
        var name = (listing.Name ?? "").Trim();
        if (name.Length < 3 || name.Length > 48) throw new ServiceException(400, "Server names are 3–48 characters.");
        if (listing.Port <= 0 || listing.Port > 65535) throw new ServiceException(400, "Invalid port.");
        if (string.IsNullOrWhiteSpace(listing.Host) || listing.Host.Length > 253) throw new ServiceException(400, "Invalid host.");
        if (listing.MaxPopulation <= 0 || listing.MaxPopulation > 512) throw new ServiceException(400, "Max players must be 1–512.");
        lock (_lock)
        {
            if (_store.Value.Servers.Count(s => s.OwnerAccountId == owner.AccountId) >= MaxServersPerAccount)
                throw new ServiceException(403, "Server limit reached for this account.");
            listing.ServerId = "srv_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            listing.Name = name;
            listing.Owner = owner.DisplayName;
            listing.Population = 0;
            listing.Kind = ServerKind.Community;
            var record = new ServerRecord { Listing = listing, OwnerAccountId = owner.AccountId, CreatedUnix = _now() };
            _store.Value.Servers.Add(record);
            _store.Save();
            return (record, Convert.ToBase64String(TicketCodec.DeriveServerKey(_masterSecret, listing.ServerId)));
        }
    }

    public void Heartbeat(string serverId, string? keyBase64, int players, int maxPlayers)
    {
        var record = Find(serverId) ?? throw new ServiceException(404, "Unknown server.");
        byte[] presented;
        try { presented = Convert.FromBase64String(keyBase64 ?? ""); }
        catch (FormatException) { throw new ServiceException(401, "Bad server key."); }
        if (!CryptographicOperations.FixedTimeEquals(presented, TicketCodec.DeriveServerKey(_masterSecret, serverId))) throw new ServiceException(401, "Bad server key.");
        lock (_lock)
        {
            record.LastHeartbeatUnix = _now();
            record.Listing.Population = Math.Max(0, Math.Min(players, 1000));
            if (maxPlayers > 0 && maxPlayers <= 512) record.Listing.MaxPopulation = maxPlayers;
            _store.Save();
        }
    }

    /// <summary>Servers that have sent a heartbeat recently.</summary>
    public List<ServerListing> Online()
    {
        var now = _now();
        lock (_lock)
            return _store.Value.Servers.Where(s => now - s.LastHeartbeatUnix <= OnlineWindowSeconds).Select(s => s.Listing).OrderByDescending(l => l.Population).ThenBy(l => l.ServerId).ToList();
    }

    public string IssueTicket(AccountRecord account, string serverId)
    {
        var record = Find(serverId) ?? throw new ServiceException(404, "Unknown server.");
        if (_now() - record.LastHeartbeatUnix > OnlineWindowSeconds) throw new ServiceException(409, "That server is offline.");
        return TicketCodec.Issue(TicketCodec.DeriveServerKey(_masterSecret, serverId), new Ticket
        {
            AccountId = account.AccountId, DisplayName = account.DisplayName, Audience = serverId, ExpiresUnix = _now() + TicketSeconds, Nonce = TicketCodec.NewNonce(),
        });
    }

    public ServerRecord? Find(string serverId)
    {
        lock (_lock) return _store.Value.Servers.FirstOrDefault(s => s.Listing.ServerId == serverId);
    }
}

/// <summary>Fixed-window request limiter keyed by caller (IP), for login/register endpoints.</summary>
public sealed class RateLimiter
{
    private readonly int _limit;
    private readonly long _windowSeconds;
    private readonly Func<long> _now;
    private readonly Dictionary<string, (long window, int count)> _counts = new();

    public RateLimiter(int limit, long windowSeconds, Func<long>? now = null)
    {
        _limit = limit;
        _windowSeconds = windowSeconds;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public bool Allow(string key)
    {
        var window = _now() / _windowSeconds;
        lock (_counts)
        {
            if (_counts.Count > 100_000) _counts.Clear();
            if (!_counts.TryGetValue(key, out var e) || e.window != window) e = (window, 0);
            e.count++;
            _counts[key] = e;
            return e.count <= _limit;
        }
    }
}
