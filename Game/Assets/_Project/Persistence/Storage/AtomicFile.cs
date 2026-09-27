using System.IO;
using System.Text;

namespace HeroGame.Persistence.Storage
{
    /// <summary>
    /// Crash-safe file writes: write to a temp file, flush to disk, then atomically replace.
    /// A reader never observes a half-written file.
    /// </summary>
    public static class AtomicFile
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        public static void WriteAllText(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        public static string ReadAllText(string path) => File.ReadAllText(path, Utf8NoBom);
    }
}
