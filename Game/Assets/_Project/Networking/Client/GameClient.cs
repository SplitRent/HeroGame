using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HeroGame.Core.Foundation;
using HeroGame.Networking.Protocol;

namespace HeroGame.Networking.Client
{
    public enum ClientState
    {
        Disconnected,
        Connecting,
        Handshaking,
        Connected,
    }

    /// <summary>
    /// Game client connection. Messages are received on a background thread and delivered on the caller's thread
    /// by <see cref="Pump"/> (Unity: from Update), so game code never sees threads. Requests return tasks that
    /// complete when the matching response arrives.
    /// </summary>
    public sealed class GameClient : IDisposable
    {
        private readonly ConcurrentQueue<NetMessage> _inbox = new ConcurrentQueue<NetMessage>();
        private readonly Dictionary<int, TaskCompletionSource<Response>> _pending = new Dictionary<int, TaskCompletionSource<Response>>();
        private readonly object _sendLock = new object();
        private TcpClient _tcp;
        private NetworkStream _stream;
        private int _nextRequest;
        private int _sequence;
        private volatile string _disconnectReason;

        public ClientState State { get; private set; }
        public Welcome Welcome { get; private set; }
        public Snapshot LastSnapshot { get; private set; }
        public string RejectReason { get; private set; } = "";
        public string DisconnectReason => _disconnectReason ?? "";

        public event Action<Welcome> Connected;
        public event Action<Snapshot> SnapshotReceived;
        public event Action<ChatMessage> ChatReceived;
        public event Action<Notice> NoticeReceived;
        public event Action<string> Disconnected;

        /// <summary>Connects and sends the join ticket. Completes when the TCP connection is up; watch <see cref="Connected"/>.</summary>
        public async Task ConnectAsync(string host, int port, string ticket, string clientVersion = "dev", bool sendHello = true)
        {
            State = ClientState.Connecting;
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(host, port).ConfigureAwait(false);
            _stream = _tcp.GetStream();
            State = ClientState.Handshaking;
            var reader = new Thread(ReadLoop) { IsBackground = true, Name = "net-client-read" };
            reader.Start();
            if (sendHello) Send(new Hello { Ticket = ticket, ClientVersion = clientVersion });
        }

        private void ReadLoop()
        {
            try
            {
                while (true)
                {
                    var msg = Wire.ReadFrame(_stream);
                    if (msg == null) break;
                    _inbox.Enqueue(msg);
                }
                _disconnectReason = _disconnectReason ?? "Server closed the connection.";
            }
            catch (Exception ex)
            {
                _disconnectReason = _disconnectReason ?? "Connection lost: " + ex.Message;
            }
            _inbox.Enqueue(null);
        }

        /// <summary>Delivers received messages and events on this thread.</summary>
        public void Pump()
        {
            while (_inbox.TryDequeue(out var msg))
            {
                if (msg == null)
                {
                    Close(_disconnectReason);
                    continue;
                }
                switch (msg)
                {
                    case Welcome w:
                        Welcome = w;
                        State = ClientState.Connected;
                        Connected?.Invoke(w);
                        break;
                    case Reject r:
                        RejectReason = r.Reason;
                        _disconnectReason = r.Reason;
                        break;
                    case Kick k:
                        _disconnectReason = k.Reason;
                        break;
                    case Snapshot s:
                        LastSnapshot = s;
                        SnapshotReceived?.Invoke(s);
                        break;
                    case Response resp:
                        TaskCompletionSource<Response> tcs;
                        lock (_pending)
                        {
                            _pending.TryGetValue(resp.RequestId, out tcs);
                            _pending.Remove(resp.RequestId);
                        }
                        tcs?.TrySetResult(resp);
                        break;
                    case ChatMessage chat:
                        ChatReceived?.Invoke(chat);
                        break;
                    case Notice notice:
                        NoticeReceived?.Invoke(notice);
                        break;
                }
            }
        }

        public void SendState(WorldPosition position, float heading, float speed, EntityId vehicle = default)
        {
            if (State != ClientState.Connected) return;
            Send(new PlayerState { Sequence = ++_sequence, Position = position, Heading = heading, Speed = speed, Vehicle = vehicle });
        }

        public void Chat(ChatChannel channel, string text) => Send(new ChatSend { Channel = channel, Text = text ?? "" });

        /// <summary>Sends a request; the task completes on <see cref="Pump"/> when the server answers (or fails on disconnect).</summary>
        public Task<Response> Request(string op, Dictionary<string, string> args = null, string idempotencyKey = null)
        {
            var id = Interlocked.Increment(ref _nextRequest);
            var tcs = new TaskCompletionSource<Response>();
            lock (_pending) _pending[id] = tcs;
            Send(new Request { RequestId = id, Op = op, Key = idempotencyKey ?? Guid.NewGuid().ToString("N"), Args = args ?? new Dictionary<string, string>() });
            return tcs.Task;
        }

        /// <summary>Sends any message (tests use this to probe server validation).</summary>
        public void Send(NetMessage message)
        {
            if (_stream == null) return;
            var frame = Wire.Frame(message);
            try
            {
                lock (_sendLock) _stream.Write(frame, 0, frame.Length);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                _disconnectReason = _disconnectReason ?? "Send failed.";
            }
        }

        /// <summary>Writes raw bytes (tests: malformed frames).</summary>
        public void SendRaw(byte[] bytes)
        {
            lock (_sendLock) _stream.Write(bytes, 0, bytes.Length);
        }

        private void Close(string reason)
        {
            if (State == ClientState.Disconnected) return;
            State = ClientState.Disconnected;
            try { _tcp?.Close(); } catch (Exception) { /* already closed */ }
            lock (_pending)
            {
                foreach (var t in _pending.Values) t.TrySetResult(new Response { Success = false, Error = "Disconnected." });
                _pending.Clear();
            }
            Disconnected?.Invoke(reason ?? "");
        }

        public void Dispose() => Close("Client closed.");
    }
}
