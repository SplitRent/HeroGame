using HeroGame.MasterServer;

// herogame-master — accounts, server list, join tickets.
// Environment: HEROGAME_MASTER_SECRET (base64, ≥ 32 bytes, required), HEROGAME_DATA (default ./master-data), HEROGAME_URLS.
var secretText = Environment.GetEnvironmentVariable("HEROGAME_MASTER_SECRET");
if (string.IsNullOrEmpty(secretText))
{
    Console.Error.WriteLine("HEROGAME_MASTER_SECRET is not set. Generate one with: openssl rand -base64 48");
    return 2;
}
byte[] secret;
try { secret = Convert.FromBase64String(secretText); }
catch (FormatException)
{
    Console.Error.WriteLine("HEROGAME_MASTER_SECRET must be base64.");
    return 2;
}
var options = new MasterOptions
{
    Secret = secret,
    DataDirectory = Environment.GetEnvironmentVariable("HEROGAME_DATA") ?? "master-data",
    Urls = Environment.GetEnvironmentVariable("HEROGAME_URLS") ?? "http://0.0.0.0:5080",
};
var app = MasterApp.Build(options, args);
Console.WriteLine("Master server listening on " + options.Urls + ", data in " + Path.GetFullPath(options.DataDirectory));
app.Run();
return 0;
