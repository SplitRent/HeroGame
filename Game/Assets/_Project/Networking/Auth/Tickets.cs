using System;
using System.Security.Cryptography;
using System.Text;

namespace HeroGame.Networking.Auth
{
    /// <summary>
    /// A signed statement from the master server: "account A (display name N) may join server S until T".
    /// Game servers verify it offline with their own key; they never see passwords.
    /// </summary>
    public sealed class Ticket
    {
        public string AccountId = "";
        public string DisplayName = "";
        /// <summary>Audience: the one server this ticket is valid for ("master" for account session tokens).</summary>
        public string Audience = "";
        public long ExpiresUnix;
        public string Nonce = "";
    }

    /// <summary>
    /// HMAC-SHA256 tickets: <c>base64url(payload).base64url(mac)</c>. Each game server gets its own key derived from
    /// the master secret (<see cref="DeriveServerKey"/>), so a community-hosted server can verify tickets for
    /// itself but cannot forge tickets for any other server or for the master.
    /// </summary>
    public static class TicketCodec
    {
        public const string MasterAudience = "master";
        private const string Version = "v1";

        public static byte[] DeriveServerKey(byte[] masterSecret, string serverId)
        {
            using (var h = new HMACSHA256(masterSecret))
                return h.ComputeHash(Encoding.UTF8.GetBytes("server-key|" + serverId));
        }

        public static string Issue(byte[] key, Ticket t)
        {
            if (Contains(t.AccountId) || Contains(t.DisplayName) || Contains(t.Audience) || Contains(t.Nonce)) throw new ArgumentException("Ticket fields may not contain '|'.");
            var payload = Version + "|" + t.AccountId + "|" + t.DisplayName + "|" + t.Audience + "|" + t.ExpiresUnix + "|" + t.Nonce;
            var bytes = Encoding.UTF8.GetBytes(payload);
            return B64(bytes) + "." + B64(Mac(key, bytes));
        }

        public static bool TryVerify(byte[] key, string text, string expectedAudience, long nowUnix, out Ticket ticket, out string error)
        {
            ticket = null;
            error = null;
            if (string.IsNullOrEmpty(text) || text.Length > 1024) { error = "Missing ticket."; return false; }
            var dot = text.IndexOf('.');
            if (dot <= 0 || dot == text.Length - 1) { error = "Malformed ticket."; return false; }
            byte[] payload, mac;
            try
            {
                payload = FromB64(text.Substring(0, dot));
                mac = FromB64(text.Substring(dot + 1));
            }
            catch (FormatException)
            {
                error = "Malformed ticket.";
                return false;
            }
            if (!FixedTimeEquals(mac, Mac(key, payload))) { error = "Ticket signature is invalid."; return false; }
            var parts = Encoding.UTF8.GetString(payload).Split('|');
            if (parts.Length != 6 || parts[0] != Version || !long.TryParse(parts[4], out var expires)) { error = "Malformed ticket."; return false; }
            var t = new Ticket { AccountId = parts[1], DisplayName = parts[2], Audience = parts[3], ExpiresUnix = expires, Nonce = parts[5] };
            if (t.Audience != expectedAudience) { error = "Ticket is for another server."; return false; }
            if (nowUnix >= t.ExpiresUnix) { error = "Ticket expired."; return false; }
            if (string.IsNullOrEmpty(t.AccountId)) { error = "Ticket has no account."; return false; }
            ticket = t;
            return true;
        }

        private static bool Contains(string s) => s != null && s.IndexOf('|') >= 0;

        private static byte[] Mac(byte[] key, byte[] data)
        {
            using (var h = new HMACSHA256(key)) return h.ComputeHash(data);
        }

        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static byte[] FromB64(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException();
            }
            return Convert.FromBase64String(s);
        }

        public static string NewNonce()
        {
            var bytes = new byte[12];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return B64(bytes);
        }
    }
}
