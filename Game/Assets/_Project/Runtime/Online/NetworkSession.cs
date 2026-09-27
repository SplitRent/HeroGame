using System.Collections.Generic;
using System.Threading.Tasks;
using HeroGame.Core.Foundation;
using HeroGame.Networking.Client;
using HeroGame.Networking.Protocol;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Player;
using HeroGame.Runtime.UI;
using UnityEngine;

namespace HeroGame.Runtime.Online
{
    /// <summary>
    /// Client side of a player-server session (GDD §4, TDD §8): signs in to the master server, fetches a join
    /// ticket, connects, streams the local player's movement at <see cref="SendRate"/>, applies the server's
    /// corrections, and shows other players from snapshots. All state changes go through <see cref="Request"/>
    /// to the authoritative server. Messages are delivered on the main thread from <see cref="Update"/>.
    /// </summary>
    public sealed class NetworkSession : MonoBehaviour
    {
        public string MasterUrl = "http://127.0.0.1:5080";
        public Transform LocalPlayer;
        public RemotePlayerPresenter Remotes;
        public float SendRate = 10f;
        public float CorrectionSnapMetres = 1.5f;

        private GameClient _client;
        private MasterClient _master;
        private float _sendTimer;
        private Vector3 _lastSent;

        public ClientState State => _client != null ? _client.State : ClientState.Disconnected;
        public string Status { get; private set; } = "Offline";

        public static NetworkSession Current { get; private set; }

        private void Awake() => Current = this;

        /// <summary>Signs in and joins <paramref name="serverId"/>. Errors end up in <see cref="Status"/>.</summary>
        public async Task Join(string username, string password, string serverId)
        {
            try
            {
                Status = "Signing in…";
                _master?.Dispose();
                _master = new MasterClient(MasterUrl);
                await _master.Login(username, password);
                Status = "Requesting a ticket…";
                var ticket = await _master.RequestTicket(serverId);
                await JoinDirect(ticket.Host, ticket.Port, ticket.Ticket);
            }
            catch (MasterServerException ex)
            {
                Status = ex.Message;
            }
            catch (System.Exception ex)
            {
                Status = "Could not connect: " + ex.Message;
            }
        }

        /// <summary>LAN/dev: connect with a ticket minted by the server console.</summary>
        public async Task JoinDirect(string host, int port, string ticket)
        {
            _client?.Dispose();
            _client = new GameClient();
            _client.Connected += OnWelcome;
            _client.SnapshotReceived += OnSnapshot;
            _client.ChatReceived += m => SubtitleFeed.Say(m.FromName, m.Text, 5f);
            _client.NoticeReceived += n => SubtitleFeed.Say(n.From, n.Text, 6f);
            _client.Disconnected += reason => Status = "Disconnected: " + reason;
            Status = "Connecting…";
            await _client.ConnectAsync(host, port, ticket, Application.version);
        }

        public Task<Response> Request(string op, Dictionary<string, string> args = null)
        {
            if (_client == null || _client.State != ClientState.Connected) return Task.FromResult(new Response { Success = false, Error = "Not connected." });
            return _client.Request(op, args);
        }

        public void Chat(string text, ChatChannel channel = ChatChannel.Local) => _client?.Chat(channel, text);

        private void OnWelcome(Welcome w)
        {
            Status = "Connected to " + w.ServerName;
            if (LocalPlayer != null && w.Position.X * w.Position.X + w.Position.Z * w.Position.Z > 1f) Warp(w.Position.ToVector3());
            // Follow the server's clock so day/night, weather and NPC schedules line up.
            if (ServiceRegistry.TryGet<GameSession>(out var session))
            {
                var delta = w.WorldSecond - session.World.Clock.Now.TotalSeconds;
                if (delta > 0) session.World.Clock.AdvanceGame(delta);
                session.World.Clock.TimeScale = w.TimeScale;
            }
        }

        private void OnSnapshot(Snapshot s)
        {
            if (s.Corrected && LocalPlayer != null && Vector3.Distance(LocalPlayer.position, s.YourPosition.ToVector3()) > CorrectionSnapMetres)
                Warp(s.YourPosition.ToVector3());
            if (Remotes != null) Remotes.Apply(s);
        }

        private void Warp(Vector3 position)
        {
            var motor = LocalPlayer.GetComponent<PlayerMotor>();
            if (motor != null) motor.Warp(position + Vector3.up * 0.1f, LocalPlayer.rotation);
            else LocalPlayer.position = position;
        }

        private void Update()
        {
            if (_client == null) return;
            _client.Pump();
            if (_client.State != ClientState.Connected || LocalPlayer == null) return;
            _sendTimer -= Time.deltaTime;
            if (_sendTimer > 0f) return;
            _sendTimer = 1f / Mathf.Max(1f, SendRate);
            var pos = LocalPlayer.position;
            var speed = (pos - _lastSent).magnitude * SendRate;
            _lastSent = pos;
            _client.SendState(pos.ToWorld(), LocalPlayer.eulerAngles.y, speed);
        }

        private void OnDestroy()
        {
            _client?.Dispose();
            _master?.Dispose();
            if (Current == this) Current = null;
        }
    }
}
