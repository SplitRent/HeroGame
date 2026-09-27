using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Networking.Protocol
{
    public enum MessageType : ushort
    {
        Hello = 1,
        Welcome = 2,
        Reject = 3,
        Ping = 4,
        Pong = 5,
        PlayerState = 10,
        Snapshot = 11,
        Request = 20,
        Response = 21,
        ChatSend = 30,
        ChatMessage = 31,
        Notice = 40,
        Kick = 50,
    }

    public abstract class NetMessage
    {
        public abstract MessageType Type { get; }
        public abstract void Write(PacketWriter w);
        public abstract void Read(PacketReader r);

        public static NetMessage Create(MessageType type)
        {
            switch (type)
            {
                case MessageType.Hello: return new Hello();
                case MessageType.Welcome: return new Welcome();
                case MessageType.Reject: return new Reject();
                case MessageType.Ping: return new Ping();
                case MessageType.Pong: return new Pong();
                case MessageType.PlayerState: return new PlayerState();
                case MessageType.Snapshot: return new Snapshot();
                case MessageType.Request: return new Request();
                case MessageType.Response: return new Response();
                case MessageType.ChatSend: return new ChatSend();
                case MessageType.ChatMessage: return new ChatMessage();
                case MessageType.Notice: return new Notice();
                case MessageType.Kick: return new Kick();
                default: return null;
            }
        }
    }

    /// <summary>Client → server, first message: protocol version and a join ticket from the master server.</summary>
    public sealed class Hello : NetMessage
    {
        public int ProtocolVersion = Wire.ProtocolVersion;
        public string Ticket = "";
        public string ClientVersion = "";
        public override MessageType Type => MessageType.Hello;
        public override void Write(PacketWriter w) { w.Int(ProtocolVersion); w.String(Ticket); w.String(ClientVersion); }
        public override void Read(PacketReader r) { ProtocolVersion = r.Int(); Ticket = r.String(1024); ClientVersion = r.String(64); }
    }

    public sealed class Welcome : NetMessage
    {
        public EntityId CharacterId;
        public string ServerName = "";
        public long WorldSecond;
        public double TimeScale;
        public WorldPosition Position;
        public long CashCents;
        public override MessageType Type => MessageType.Welcome;
        public override void Write(PacketWriter w) { w.Id(CharacterId); w.String(ServerName); w.Long(WorldSecond); w.Double(TimeScale); w.Position(Position); w.Long(CashCents); }
        public override void Read(PacketReader r) { CharacterId = r.Id(); ServerName = r.String(128); WorldSecond = r.Long(); TimeScale = r.Double(); Position = r.Position(); CashCents = r.Long(); }
    }

    public sealed class Reject : NetMessage
    {
        public string Reason = "";
        public override MessageType Type => MessageType.Reject;
        public override void Write(PacketWriter w) => w.String(Reason);
        public override void Read(PacketReader r) => Reason = r.String(512);
    }

    public sealed class Ping : NetMessage
    {
        public long Nonce;
        public override MessageType Type => MessageType.Ping;
        public override void Write(PacketWriter w) => w.Long(Nonce);
        public override void Read(PacketReader r) => Nonce = r.Long();
    }

    public sealed class Pong : NetMessage
    {
        public long Nonce;
        public override MessageType Type => MessageType.Pong;
        public override void Write(PacketWriter w) => w.Long(Nonce);
        public override void Read(PacketReader r) => Nonce = r.Long();
    }

    /// <summary>Client → server movement (~10 Hz). The server validates speed and keeps the authoritative position.</summary>
    public sealed class PlayerState : NetMessage
    {
        public int Sequence;
        public WorldPosition Position;
        public float Heading;
        public float Speed;
        public EntityId Vehicle;
        public override MessageType Type => MessageType.PlayerState;
        public override void Write(PacketWriter w) { w.Int(Sequence); w.Position(Position); w.Float(Heading); w.Float(Speed); w.Id(Vehicle); }
        public override void Read(PacketReader r) { Sequence = r.Int(); Position = r.Position(); Heading = r.Float(); Speed = r.Float(); Vehicle = r.Id(); }
    }

    public struct RemotePlayer
    {
        public EntityId CharacterId;
        public string Name;
        public WorldPosition Position;
        public float Heading;
        public float Speed;
        public byte WantedLevel;
    }

    /// <summary>Server → client world state near the player (~10 Hz).</summary>
    public sealed class Snapshot : NetMessage
    {
        public const int MaxPlayers = 128;
        public long WorldSecond;
        public byte Weather;
        public float TemperatureC;
        /// <summary>Last movement sequence the server accepted (for client reconciliation).</summary>
        public int AckSequence;
        /// <summary>Authoritative own position (differs from the client's after a correction).</summary>
        public WorldPosition YourPosition;
        public bool Corrected;
        public List<RemotePlayer> Players = new List<RemotePlayer>();
        public override MessageType Type => MessageType.Snapshot;

        public override void Write(PacketWriter w)
        {
            w.Long(WorldSecond);
            w.Byte(Weather);
            w.Float(TemperatureC);
            w.Int(AckSequence);
            w.Position(YourPosition);
            w.Bool(Corrected);
            var n = System.Math.Min(Players.Count, MaxPlayers);
            w.Byte((byte)n);
            for (var i = 0; i < n; i++)
            {
                var p = Players[i];
                w.Id(p.CharacterId);
                w.String(p.Name);
                w.Position(p.Position);
                w.Float(p.Heading);
                w.Float(p.Speed);
                w.Byte(p.WantedLevel);
            }
        }

        public override void Read(PacketReader r)
        {
            WorldSecond = r.Long();
            Weather = r.Byte();
            TemperatureC = r.Float();
            AckSequence = r.Int();
            YourPosition = r.Position();
            Corrected = r.Bool();
            var n = r.Byte();
            if (n > MaxPlayers) throw new ProtocolException("Too many players in snapshot.");
            Players.Clear();
            for (var i = 0; i < n; i++)
                Players.Add(new RemotePlayer { CharacterId = r.Id(), Name = r.String(64), Position = r.Position(), Heading = r.Float(), Speed = r.Float(), WantedLevel = r.Byte() });
        }
    }

    /// <summary>
    /// A player request (buy, build, hire, commit a crime…). The server resolves who the player is from the
    /// connection, never from arguments; <see cref="Key"/> makes retries idempotent.
    /// </summary>
    public sealed class Request : NetMessage
    {
        public int RequestId;
        public string Op = "";
        public string Key = "";
        public Dictionary<string, string> Args = new Dictionary<string, string>();
        public override MessageType Type => MessageType.Request;
        public override void Write(PacketWriter w) { w.Int(RequestId); w.String(Op); w.String(Key); w.Map(Args); }
        public override void Read(PacketReader r) { RequestId = r.Int(); Op = r.String(64); Key = r.String(128); Args = r.Map(); }
    }

    public sealed class Response : NetMessage
    {
        public int RequestId;
        public bool Success;
        public string Error = "";
        public Dictionary<string, string> Data = new Dictionary<string, string>();
        public override MessageType Type => MessageType.Response;
        public override void Write(PacketWriter w) { w.Int(RequestId); w.Bool(Success); w.String(Error); w.Map(Data); }
        public override void Read(PacketReader r) { RequestId = r.Int(); Success = r.Bool(); Error = r.String(1024); Data = r.Map(); }
    }

    public enum ChatChannel : byte
    {
        Local = 0,
        Global = 1,
        Server = 2,
    }

    public sealed class ChatSend : NetMessage
    {
        public const int MaxLength = 280;
        public ChatChannel Channel;
        public string Text = "";
        public override MessageType Type => MessageType.ChatSend;
        public override void Write(PacketWriter w) { w.Byte((byte)Channel); w.String(Text); }
        public override void Read(PacketReader r) { Channel = (ChatChannel)r.Byte(); Text = r.String(MaxLength * 4); }
    }

    public sealed class ChatMessage : NetMessage
    {
        public ChatChannel Channel;
        public string FromName = "";
        public string Text = "";
        public override MessageType Type => MessageType.ChatMessage;
        public override void Write(PacketWriter w) { w.Byte((byte)Channel); w.String(FromName); w.String(Text); }
        public override void Read(PacketReader r) { Channel = (ChatChannel)r.Byte(); FromName = r.String(128); Text = r.String(2048); }
    }

    /// <summary>Server → client push of a phone message (bank alert, court notice, news).</summary>
    public sealed class Notice : NetMessage
    {
        public byte Category;
        public string From = "";
        public string Text = "";
        public override MessageType Type => MessageType.Notice;
        public override void Write(PacketWriter w) { w.Byte(Category); w.String(From); w.String(Text); }
        public override void Read(PacketReader r) { Category = r.Byte(); From = r.String(128); Text = r.String(2048); }
    }

    public sealed class Kick : NetMessage
    {
        public string Reason = "";
        public override MessageType Type => MessageType.Kick;
        public override void Write(PacketWriter w) => w.String(Reason);
        public override void Read(PacketReader r) => Reason = r.String(512);
    }
}
