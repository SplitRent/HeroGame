using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HeroGame.Networking.Security
{
    /// <summary>
    /// Certificate pinning for community servers, which normally use self-signed certificates: the server publishes
    /// the SHA-256 of its certificate through the master listing, and clients accept exactly that certificate.
    /// </summary>
    public static class TlsPinning
    {
        /// <summary>Lower-case hex SHA-256 of the DER certificate.</summary>
        public static string Fingerprint(X509Certificate cert)
        {
            if (cert == null) return "";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(cert.GetRawCertData());
                var chars = new char[hash.Length * 2];
                for (var i = 0; i < hash.Length; i++)
                {
                    chars[i * 2] = Hex(hash[i] >> 4);
                    chars[i * 2 + 1] = Hex(hash[i] & 0xF);
                }
                return new string(chars);
            }
        }

        public static bool Matches(X509Certificate cert, string expectedFingerprint)
        {
            if (cert == null || string.IsNullOrEmpty(expectedFingerprint)) return false;
            var actual = Fingerprint(cert);
            var expected = expectedFingerprint.Replace(":", "").Trim().ToLowerInvariant();
            if (actual.Length != expected.Length) return false;
            var diff = 0;
            for (var i = 0; i < actual.Length; i++) diff |= actual[i] ^ expected[i];
            return diff == 0;
        }

        private static char Hex(int v) => (char)(v < 10 ? '0' + v : 'a' + v - 10);
    }

    /// <summary>How a client secures its connection to a game server.</summary>
    public sealed class ClientTls
    {
        /// <summary>SHA-256 fingerprint to pin (from the server listing). Empty: require a publicly trusted certificate.</summary>
        public string PinnedFingerprint = "";
        /// <summary>Name to validate for publicly trusted certificates (defaults to the host).</summary>
        public string ServerName = "";
    }
}
