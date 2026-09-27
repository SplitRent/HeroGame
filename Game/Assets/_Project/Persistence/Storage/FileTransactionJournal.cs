using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HeroGame.Core.Economy;
using HeroGame.Persistence.Json;

namespace HeroGame.Persistence.Storage
{
    /// <summary>
    /// Durable write-ahead journal: one JSON object per line, flushed to disk before
    /// <see cref="Append"/> returns (TDD §9.4). A torn final line (power loss mid-write) is ignored
    /// on read, which is correct: its transaction was never acknowledged, so it was never applied.
    /// </summary>
    public sealed class FileTransactionJournal : ITransactionJournal, IDisposable
    {
        private readonly string _path;
        private FileStream _stream;
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public FileTransactionJournal(string path)
        {
            _path = path;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            RepairTornTail();
            OpenForAppend();
        }

        /// <summary>Entries that could not be parsed on the last read (corruption indicator).</summary>
        public int CorruptEntries { get; private set; }

        public string FilePath => _path;

        public void Append(WorldTransaction tx)
        {
            var line = JsonSetup.Serialize(tx) + "\n";
            var bytes = Utf8NoBom.GetBytes(line);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush(true);
        }

        public IEnumerable<WorldTransaction> ReadAfter(long sequence)
        {
            var result = new List<WorldTransaction>();
            CorruptEntries = 0;
            _stream.Flush(true);
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs, Utf8NoBom))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    WorldTransaction tx;
                    try
                    {
                        tx = JsonSetup.Deserialize<WorldTransaction>(line);
                    }
                    catch (Exception)
                    {
                        CorruptEntries++;
                        continue;
                    }
                    if (tx != null && tx.Sequence > sequence) result.Add(tx);
                }
            }
            result.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
            return result;
        }

        public void Compact(long upTo)
        {
            var keep = ReadAfter(upTo);
            _stream.Dispose();
            var sb = new StringBuilder();
            foreach (var tx in keep) sb.Append(JsonSetup.Serialize(tx)).Append('\n');
            AtomicFile.WriteAllText(_path, sb.ToString());
            OpenForAppend();
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }

        /// <summary>
        /// Every acknowledged entry ends with a newline. Bytes after the last newline belong to an
        /// append that was interrupted and never acknowledged, so they are discarded before new
        /// entries are appended (otherwise the next entry would be glued onto the torn one).
        /// </summary>
        private void RepairTornTail()
        {
            if (!File.Exists(_path)) return;
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var length = fs.Length;
                if (length == 0) return;
                var position = length;
                var buffer = new byte[1];
                while (position > 0)
                {
                    fs.Position = position - 1;
                    fs.Read(buffer, 0, 1);
                    if (buffer[0] == (byte)'\n') break;
                    position--;
                }
                if (position != length)
                {
                    fs.SetLength(position);
                    fs.Flush(true);
                }
            }
        }

        private void OpenForAppend()
        {
            _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
    }
}
