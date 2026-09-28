using System;
using System.Collections.Generic;
using System.IO;

namespace HeroGame.Persistence.Settings
{
    using HeroGame.Core.Presentation;
    using HeroGame.Persistence.Json;
    using HeroGame.Persistence.Storage;

    /// <summary>
    /// Stores <see cref="GameSettings"/> as a flat JSON map (settings.json in the player's data folder). A missing or
    /// damaged file yields defaults; a damaged value yields that value's default. Writes are atomic.
    /// </summary>
    public sealed class SettingsStore
    {
        public const string FileName = "settings.json";
        private readonly string _path;

        public SettingsStore(string directory)
        {
            _path = Path.Combine(directory, FileName);
        }

        public string FilePath => _path;

        public GameSettings Load()
        {
            try
            {
                if (!File.Exists(_path)) return new GameSettings();
                var map = JsonSetup.Deserialize<Dictionary<string, string>>(AtomicFile.ReadAllText(_path));
                return GameSettings.FromMap(map);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is Newtonsoft.Json.JsonException)
            {
                return new GameSettings();
            }
        }

        public void Save(GameSettings settings)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
            AtomicFile.WriteAllText(_path, JsonSetup.Serialize(settings.Clamp().ToMap(), indented: true));
        }
    }
}
