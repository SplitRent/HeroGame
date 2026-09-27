using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HeroGame.Core.Foundation;

namespace HeroGame.Networking.Protocol
{
    /// <summary>Malformed or hostile input. The connection that produced it is dropped; the server keeps running.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message) { }
    }

    /// <summary>Little-endian binary writer for message payloads.</summary>
    public sealed class PacketWriter
    {
        private readonly MemoryStream _stream = new MemoryStream(256);
        private readonly BinaryWriter _writer;

        public PacketWriter()
        {
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
        }

        public void Byte(byte v) => _writer.Write(v);
        public void Bool(bool v) => _writer.Write(v);
        public void Int(int v) => _writer.Write(v);
        public void Long(long v) => _writer.Write(v);
        public void Float(float v) => _writer.Write(v);
        public void Double(double v) => _writer.Write(v);
        public void Id(EntityId id) => _writer.Write(id.Value);
        public void Position(WorldPosition p)
        {
            _writer.Write(p.X);
            _writer.Write(p.Y);
            _writer.Write(p.Z);
        }

        public void String(string s) => String(s, Wire.MaxStringBytes);

        /// <summary>A string with a caller-chosen limit (up to <see cref="Wire.MaxLongStringBytes"/>) for bulk payloads.</summary>
        public void String(string s, int maxBytes)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            if (bytes.Length > System.Math.Min(maxBytes, Wire.MaxLongStringBytes)) throw new ProtocolException("String too long to send.");
            _writer.Write((ushort)bytes.Length);
            _writer.Write(bytes);
        }

        public void Map(IReadOnlyDictionary<string, string> map)
        {
            var count = map != null ? map.Count : 0;
            if (count > Wire.MaxMapEntries) throw new ProtocolException("Too many entries.");
            _writer.Write((byte)count);
            if (map == null) return;
            var keys = new List<string>(map.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var k in keys)
            {
                String(k);
                String(map[k]);
            }
        }

        public byte[] ToArray()
        {
            _writer.Flush();
            return _stream.ToArray();
        }
    }

    /// <summary>Bounds-checked reader: every length and count is validated before allocation.</summary>
    public sealed class PacketReader
    {
        private readonly byte[] _data;
        private int _pos;

        public PacketReader(byte[] data)
        {
            _data = data ?? Array.Empty<byte>();
        }

        public int Remaining => _data.Length - _pos;

        private void Need(int n)
        {
            if (n < 0 || _pos + n > _data.Length) throw new ProtocolException("Truncated message.");
        }

        public byte Byte()
        {
            Need(1);
            return _data[_pos++];
        }

        public bool Bool() => Byte() != 0;

        public int Int()
        {
            Need(4);
            var v = BitConverter.ToInt32(_data, _pos);
            _pos += 4;
            return v;
        }

        public long Long()
        {
            Need(8);
            var v = BitConverter.ToInt64(_data, _pos);
            _pos += 8;
            return v;
        }

        public float Float()
        {
            Need(4);
            var v = BitConverter.ToSingle(_data, _pos);
            _pos += 4;
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new ProtocolException("Non-finite number.");
            return v;
        }

        public double Double()
        {
            Need(8);
            var v = BitConverter.ToDouble(_data, _pos);
            _pos += 8;
            if (double.IsNaN(v) || double.IsInfinity(v)) throw new ProtocolException("Non-finite number.");
            return v;
        }

        public EntityId Id() => new EntityId((ulong)Long());

        public WorldPosition Position() => new WorldPosition(Float(), Float(), Float());

        public string String(int maxBytes = Wire.MaxStringBytes)
        {
            Need(2);
            var len = BitConverter.ToUInt16(_data, _pos);
            _pos += 2;
            if (len > maxBytes) throw new ProtocolException("String too long.");
            Need(len);
            var s = Encoding.UTF8.GetString(_data, _pos, len);
            _pos += len;
            return s;
        }

        public Dictionary<string, string> Map()
        {
            var count = Byte();
            if (count > Wire.MaxMapEntries) throw new ProtocolException("Too many entries.");
            var map = new Dictionary<string, string>(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var k = String(64);
                var v = String(512);
                if (map.ContainsKey(k)) throw new ProtocolException("Duplicate key.");
                map[k] = v;
            }
            return map;
        }
    }

    /// <summary>
    /// Framing: [uint32 length][ushort type][payload]. Length covers type + payload and is capped so a hostile
    /// peer cannot make the server allocate large buffers.
    /// </summary>
    public static class Wire
    {
        public const int ProtocolVersion = 1;
        public const int MaxFrameBytes = 64 * 1024;
        public const int MaxStringBytes = 4096;
        /// <summary>Largest single string (building layouts); still bounded by the 64 KiB frame.</summary>
        public const int MaxLongStringBytes = 60000;
        public const int MaxMapEntries = 24;

        public static byte[] Frame(NetMessage message)
        {
            var w = new PacketWriter();
            message.Write(w);
            var payload = w.ToArray();
            var length = payload.Length + 2;
            if (length > MaxFrameBytes) throw new ProtocolException("Message too large.");
            var frame = new byte[4 + length];
            BitConverter.GetBytes(length).CopyTo(frame, 0);
            BitConverter.GetBytes((ushort)message.Type).CopyTo(frame, 4);
            payload.CopyTo(frame, 6);
            return frame;
        }

        /// <summary>Reads exactly one frame; returns null at a clean end of stream.</summary>
        public static NetMessage ReadFrame(Stream stream)
        {
            var header = new byte[4];
            if (!ReadExactly(stream, header, 4, allowEof: true)) return null;
            var length = BitConverter.ToInt32(header, 0);
            if (length < 2 || length > MaxFrameBytes) throw new ProtocolException("Bad frame length " + length + ".");
            var body = new byte[length];
            if (!ReadExactly(stream, body, length, allowEof: false)) throw new ProtocolException("Connection closed mid-frame.");
            var type = (MessageType)BitConverter.ToUInt16(body, 0);
            var payload = new byte[length - 2];
            Buffer.BlockCopy(body, 2, payload, 0, payload.Length);
            return Decode(type, payload);
        }

        public static NetMessage Decode(MessageType type, byte[] payload)
        {
            var message = NetMessage.Create(type);
            if (message == null) throw new ProtocolException("Unknown message type " + (int)type + ".");
            var r = new PacketReader(payload);
            message.Read(r);
            if (r.Remaining != 0) throw new ProtocolException("Trailing bytes in " + type + ".");
            return message;
        }

        private static bool ReadExactly(Stream s, byte[] buffer, int count, bool allowEof)
        {
            var read = 0;
            while (read < count)
            {
                var n = s.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    if (read == 0 && allowEof) return false;
                    throw new ProtocolException("Unexpected end of stream.");
                }
                read += n;
            }
            return true;
        }
    }
}
