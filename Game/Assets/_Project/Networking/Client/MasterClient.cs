using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace HeroGame.Networking.Client
{
    using HeroGame.Core.Servers;

    public sealed class LoginResult
    {
        public string AccountId = "";
        public string DisplayName = "";
        public string Token = "";
        public long ExpiresUnix;
    }

    public sealed class ServerRegistration
    {
        public string ServerId = "";
        /// <summary>Base64 ticket-verification key for this server only.</summary>
        public string ServerKey = "";
    }

    public sealed class JoinTicket
    {
        public string Ticket = "";
        public string Host = "";
        public int Port;
        /// <summary>Pin for the server's TLS certificate (empty: the server does not use TLS).</summary>
        public string TlsFingerprint = "";
    }

    /// <summary>Master-server API (TDD §8.4): accounts, the public server list, server registration/heartbeat and join tickets.</summary>
    public sealed class MasterClient : IDisposable
    {
        private readonly HttpClient _http;

        public MasterClient(string baseUrl, HttpMessageHandler handler = null)
        {
            _http = handler != null ? new HttpClient(handler) : new HttpClient();
            _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            _http.Timeout = TimeSpan.FromSeconds(10);
        }

        public string Token { get; set; } = "";

        public Task<string> Register(string username, string password, string displayName) =>
            Post<Dictionary<string, string>>("api/accounts/register", new { username, password, displayName }, auth: false).ContinueWith(t => t.Result["accountId"]);

        public async Task<LoginResult> Login(string username, string password)
        {
            var r = await Post<LoginResult>("api/accounts/login", new { username, password }, auth: false).ConfigureAwait(false);
            Token = r.Token;
            return r;
        }

        public Task<List<ServerListing>> ListServers() => Get<List<ServerListing>>("api/servers");

        /// <summary>Signs this session out on the master (the token stops working at once).</summary>
        public async Task Logout()
        {
            if (string.IsNullOrEmpty(Token)) return;
            await Post<object>("api/accounts/logout", new { }, auth: true).ConfigureAwait(false);
            Token = null;
        }

        public Task<ServerRegistration> RegisterServer(ServerListing listing) => Post<ServerRegistration>("api/servers", listing, auth: true);

        public async Task Heartbeat(string serverId, string serverKey, int players, int maxPlayers, string tlsFingerprint = null)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, "api/servers/" + Uri.EscapeDataString(serverId) + "/heartbeat"))
            {
                req.Headers.Add("X-Server-Key", serverKey);
                req.Content = Json(new { players, maxPlayers, tlsFingerprint });
                using (var resp = await _http.SendAsync(req).ConfigureAwait(false)) await Ensure(resp).ConfigureAwait(false);
            }
        }

        public Task<JoinTicket> RequestTicket(string serverId) => Post<JoinTicket>("api/servers/" + Uri.EscapeDataString(serverId) + "/ticket", new { }, auth: true);

        private async Task<T> Get<T>(string path)
        {
            using (var resp = await _http.GetAsync(path).ConfigureAwait(false))
            {
                await Ensure(resp).ConfigureAwait(false);
                return JsonConvert.DeserializeObject<T>(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }

        private async Task<T> Post<T>(string path, object body, bool auth)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Post, path))
            {
                if (auth) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
                req.Content = Json(body);
                using (var resp = await _http.SendAsync(req).ConfigureAwait(false))
                {
                    await Ensure(resp).ConfigureAwait(false);
                    return JsonConvert.DeserializeObject<T>(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
        }

        private static StringContent Json(object body) => new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        private static async Task Ensure(HttpResponseMessage resp)
        {
            if (resp.IsSuccessStatusCode) return;
            var text = resp.Content != null ? await resp.Content.ReadAsStringAsync().ConfigureAwait(false) : "";
            throw new MasterServerException((int)resp.StatusCode, string.IsNullOrEmpty(text) ? resp.ReasonPhrase : text);
        }

        public void Dispose() => _http.Dispose();
    }

    public sealed class MasterServerException : Exception
    {
        public readonly int Status;

        public MasterServerException(int status, string message) : base(message)
        {
            Status = status;
        }
    }
}
