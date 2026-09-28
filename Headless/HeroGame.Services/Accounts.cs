using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HeroGame.Networking.Auth;

namespace HeroGame.Services;

public sealed class AccountRecord
{
    public string AccountId = "";
    public string Username = "";
    public string DisplayName = "";
    public string PasswordHash = "";
    public string Salt = "";
    public int Iterations;
    public long CreatedUnix;
    public int FailedLogins;
    public long LockedUntilUnix;
    /// <summary>Sessions issued before this moment are void ("sign out everywhere", password change).</summary>
    public long SessionsValidFromUnix;
}

public sealed class RevokedSession
{
    public string Nonce = "";
    public long ExpiresUnix;
}

public sealed class AccountsDocument
{
    public List<AccountRecord> Accounts = new();
    /// <summary>Signed-out session tokens that have not expired yet.</summary>
    public List<RevokedSession> RevokedSessions = new();
}

public sealed class ServiceException : Exception
{
    public readonly int Status;
    public ServiceException(int status, string message) : base(message) => Status = status;
}

/// <summary>PBKDF2-SHA256 password hashing (salted, 210k iterations per OWASP 2023 guidance).</summary>
public static class PasswordHasher
{
    public const int Iterations = 210_000;

    public static (string hash, string salt) Hash(string password, int iterations = Iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Convert.ToBase64String(Derive(password, salt, iterations)), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string hash, string salt, int iterations)
    {
        var expected = Convert.FromBase64String(hash);
        var actual = Derive(password, Convert.FromBase64String(salt), iterations);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
}

/// <summary>
/// Global accounts (GDD §5): one identity across every server. Usernames are case-insensitive; passwords are
/// never stored; repeated failures lock the account briefly; sessions are master-signed tickets.
/// </summary>
public sealed class AccountDirectory
{
    public const int MaxFailedLogins = 5;
    public const long LockoutSeconds = 300;
    public const long SessionSeconds = 12 * 3600;
    private static readonly Regex UsernamePattern = new("^[a-z0-9_]{3,20}$", RegexOptions.Compiled);

    private readonly JsonFileStore<AccountsDocument> _store;
    private readonly byte[] _masterSecret;
    private readonly Func<long> _now;
    private readonly int _iterations;
    private readonly object _lock = new();

    public AccountDirectory(JsonFileStore<AccountsDocument> store, byte[] masterSecret, Func<long>? now = null, int iterations = PasswordHasher.Iterations)
    {
        _store = store;
        _masterSecret = masterSecret;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _iterations = iterations;
    }

    public int Count
    {
        get { lock (_lock) return _store.Value.Accounts.Count; }
    }

    public AccountRecord Register(string username, string password, string displayName)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        displayName = (displayName ?? "").Trim();
        if (!UsernamePattern.IsMatch(username)) throw new ServiceException(400, "Usernames are 3–20 lowercase letters, digits or underscores.");
        if ((password ?? "").Length < 10) throw new ServiceException(400, "Passwords must be at least 10 characters.");
        if (password!.Length > 256) throw new ServiceException(400, "Password too long.");
        if (displayName.Length < 2 || displayName.Length > 32 || displayName.Contains('|') || displayName.Any(char.IsControl))
            throw new ServiceException(400, "Display names are 2–32 printable characters.");
        var (hash, salt) = PasswordHasher.Hash(password, _iterations);
        lock (_lock)
        {
            if (_store.Value.Accounts.Any(a => a.Username == username)) throw new ServiceException(409, "That username is taken.");
            var record = new AccountRecord
            {
                AccountId = "acc_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                Username = username, DisplayName = displayName, PasswordHash = hash, Salt = salt, Iterations = _iterations, CreatedUnix = _now(),
            };
            _store.Value.Accounts.Add(record);
            _store.Save();
            return record;
        }
    }

    public (AccountRecord account, string token, long expires) Login(string username, string password)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        AccountRecord? record;
        lock (_lock) record = _store.Value.Accounts.FirstOrDefault(a => a.Username == username);
        // Same work whether or not the user exists, so timing does not reveal valid usernames.
        if (record == null)
        {
            PasswordHasher.Verify(password ?? "", Convert.ToBase64String(new byte[32]), Convert.ToBase64String(new byte[16]), _iterations);
            throw new ServiceException(401, "Wrong username or password.");
        }
        var now = _now();
        if (record.LockedUntilUnix > now) throw new ServiceException(429, "Too many failed attempts. Try again in a few minutes.");
        if (!PasswordHasher.Verify(password ?? "", record.PasswordHash, record.Salt, record.Iterations))
        {
            lock (_lock)
            {
                record.FailedLogins++;
                if (record.FailedLogins >= MaxFailedLogins)
                {
                    record.LockedUntilUnix = now + LockoutSeconds;
                    record.FailedLogins = 0;
                }
                _store.Save();
            }
            throw new ServiceException(401, "Wrong username or password.");
        }
        lock (_lock)
        {
            record.FailedLogins = 0;
            _store.Save();
        }
        var expires = now + SessionSeconds;
        var token = TicketCodec.Issue(_masterSecret, new Ticket
        {
            AccountId = record.AccountId, DisplayName = record.DisplayName, Audience = TicketCodec.MasterAudience, ExpiresUnix = expires, Nonce = TicketCodec.NewNonce(),
        });
        return (record, token, expires);
    }

    /// <summary>Resolves a bearer session token to its account (401 if invalid, expired or signed out).</summary>
    public AccountRecord Authenticate(string? token) => Session(token).account;

    private (AccountRecord account, Ticket session) Session(string? token)
    {
        if (!TicketCodec.TryVerify(_masterSecret, token ?? "", TicketCodec.MasterAudience, _now(), out var t, out var error))
            throw new ServiceException(401, error ?? "Not signed in.");
        lock (_lock)
        {
            var record = _store.Value.Accounts.FirstOrDefault(a => a.AccountId == t.AccountId) ?? throw new ServiceException(401, "Account no longer exists.");
            if (t.ExpiresUnix - SessionSeconds < record.SessionsValidFromUnix) throw new ServiceException(401, "This session was signed out.");
            if (_store.Value.RevokedSessions.Any(r => r.Nonce == t.Nonce)) throw new ServiceException(401, "This session was signed out.");
            return (record, t);
        }
    }

    /// <summary>Signs this session out: the token stops working immediately, not at expiry.</summary>
    public void Logout(string? token)
    {
        var (_, t) = Session(token);
        lock (_lock)
        {
            var now = _now();
            _store.Value.RevokedSessions.RemoveAll(r => r.ExpiresUnix <= now);
            _store.Value.RevokedSessions.Add(new RevokedSession { Nonce = t.Nonce, ExpiresUnix = t.ExpiresUnix });
            _store.Save();
        }
    }

    /// <summary>Signs out every session of the account (lost device, suspected theft).</summary>
    public void LogoutEverywhere(string? token)
    {
        var (record, t) = Session(token);
        lock (_lock)
        {
            record.SessionsValidFromUnix = Math.Max(record.SessionsValidFromUnix, t.ExpiresUnix - SessionSeconds + 1);
            record.SessionsValidFromUnix = Math.Max(record.SessionsValidFromUnix, _now());
            _store.Save();
        }
    }

    /// <summary>Changes the password (the current one is required) and signs out every existing session.</summary>
    public void ChangePassword(string? token, string currentPassword, string newPassword)
    {
        var (record, t) = Session(token);
        if (!PasswordHasher.Verify(currentPassword ?? "", record.PasswordHash, record.Salt, record.Iterations)) throw new ServiceException(401, "Wrong password.");
        if ((newPassword ?? "").Length < 10) throw new ServiceException(400, "Passwords must be at least 10 characters.");
        if (newPassword!.Length > 256) throw new ServiceException(400, "Password too long.");
        var (hash, salt) = PasswordHasher.Hash(newPassword, _iterations);
        lock (_lock)
        {
            record.PasswordHash = hash;
            record.Salt = salt;
            record.Iterations = _iterations;
            record.SessionsValidFromUnix = Math.Max(_now(), t.ExpiresUnix - SessionSeconds + 1);
            _store.Save();
        }
    }
}
