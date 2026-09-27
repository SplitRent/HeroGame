using System.Text.Json;
using HeroGame.Core.Servers;
using HeroGame.Services;

namespace HeroGame.MasterServer;

public sealed class MasterOptions
{
    /// <summary>≥ 32 random bytes. Signs sessions and derives every server's ticket key. Keep it secret.</summary>
    public byte[] Secret = Array.Empty<byte>();
    /// <summary>Directory for accounts.json and servers.json (null = in memory, for tests).</summary>
    public string? DataDirectory;
    public string Urls = "http://0.0.0.0:5080";
    /// <summary>PBKDF2 iterations (tests lower this; production keeps the default).</summary>
    public int PasswordIterations = PasswordHasher.Iterations;
}

/// <summary>
/// Master server HTTP API (TDD §8.4). Endpoints are thin wrappers over <see cref="AccountDirectory"/> and
/// <see cref="ServerDirectory"/>; errors map to status codes with a plain-text reason.
/// </summary>
public static class MasterApp
{
    public static WebApplication Build(MasterOptions o, string[]? args = null)
    {
        if (o.Secret.Length < 32) throw new ArgumentException("The master secret must be at least 32 bytes.");
        var builder = WebApplication.CreateBuilder(args ?? Array.Empty<string>());
        builder.WebHost.UseUrls(o.Urls);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(j => j.SerializerOptions.IncludeFields = true);
        var app = builder.Build();

        string? Path(string file) => o.DataDirectory == null ? null : System.IO.Path.Combine(o.DataDirectory, file);
        var accounts = new AccountDirectory(new JsonFileStore<AccountsDocument>(Path("accounts.json")), o.Secret, iterations: o.PasswordIterations);
        var servers = new ServerDirectory(new JsonFileStore<ServersDocument>(Path("servers.json")), o.Secret);
        var authLimiter = new RateLimiter(20, 60);

        string Caller(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
        string? Bearer(HttpContext ctx)
        {
            var h = ctx.Request.Headers.Authorization.ToString();
            return h.StartsWith("Bearer ", StringComparison.Ordinal) ? h.Substring(7) : null;
        }
        IResult Run(Func<object?> action)
        {
            try
            {
                var result = action();
                return result == null ? Results.NoContent() : Results.Json(result, new JsonSerializerOptions { IncludeFields = true });
            }
            catch (ServiceException ex)
            {
                return Results.Text(ex.Message, statusCode: ex.Status);
            }
        }

        app.MapGet("/health", () => Results.Text("ok"));

        app.MapPost("/api/accounts/register", (HttpContext ctx, RegisterBody body) =>
        {
            if (!authLimiter.Allow("register:" + Caller(ctx))) return Results.Text("Slow down.", statusCode: 429);
            return Run(() => new Dictionary<string, string> { ["accountId"] = accounts.Register(body.Username ?? "", body.Password ?? "", body.DisplayName ?? "").AccountId });
        });

        app.MapPost("/api/accounts/login", (HttpContext ctx, LoginBody body) =>
        {
            if (!authLimiter.Allow("login:" + Caller(ctx))) return Results.Text("Slow down.", statusCode: 429);
            return Run(() =>
            {
                var (account, token, expires) = accounts.Login(body.Username ?? "", body.Password ?? "");
                return new Dictionary<string, object> { ["AccountId"] = account.AccountId, ["DisplayName"] = account.DisplayName, ["Token"] = token, ["ExpiresUnix"] = expires };
            });
        });

        app.MapGet("/api/servers", () => Run(() => servers.Online()));

        app.MapPost("/api/servers", (HttpContext ctx, ServerListing listing) => Run(() =>
        {
            var owner = accounts.Authenticate(Bearer(ctx));
            var (record, key) = servers.Register(owner, listing);
            return new Dictionary<string, string> { ["ServerId"] = record.Listing.ServerId, ["ServerKey"] = key };
        }));

        app.MapPost("/api/servers/{id}/heartbeat", (HttpContext ctx, string id, HeartbeatBody body) => Run(() =>
        {
            servers.Heartbeat(id, ctx.Request.Headers["X-Server-Key"].ToString(), body.Players, body.MaxPlayers);
            return null;
        }));

        app.MapPost("/api/servers/{id}/ticket", (HttpContext ctx, string id) => Run(() =>
        {
            var account = accounts.Authenticate(Bearer(ctx));
            var ticket = servers.IssueTicket(account, id);
            var listing = servers.Find(id)!.Listing;
            return new Dictionary<string, object> { ["Ticket"] = ticket, ["Host"] = listing.Host, ["Port"] = listing.Port };
        }));

        return app;
    }

    public sealed class RegisterBody
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? DisplayName { get; set; }
    }

    public sealed class LoginBody
    {
        public string? Username { get; set; }
        public string? Password { get; set; }
    }

    public sealed class HeartbeatBody
    {
        public int Players { get; set; }
        public int MaxPlayers { get; set; }
    }
}
