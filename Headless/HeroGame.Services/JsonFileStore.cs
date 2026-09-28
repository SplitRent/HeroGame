using System.Text.Json;

namespace HeroGame.Services;

/// <summary>Small JSON document store with atomic replace-on-write (write temp, fsync, rename).</summary>
public sealed class JsonFileStore<T> where T : new()
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IncludeFields = true };
    private readonly string? _path;
    private readonly object _lock = new();

    public JsonFileStore(string? path)
    {
        _path = path;
        Value = Load();
    }

    public T Value { get; }

    private T Load()
    {
        if (_path == null || !File.Exists(_path)) return new T();
        return JsonSerializer.Deserialize<T>(File.ReadAllText(_path), Options) ?? new T();
    }

    public void Save()
    {
        if (_path == null) return;
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, Value, Options);
                fs.Flush(true);
            }
            File.Move(temp, _path, overwrite: true);
        }
    }
}
