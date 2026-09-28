using System;
using System.IO;

namespace HeroGame.Tests
{
    using HeroGame.Core.Config;
    using HeroGame.Core.World;
    using HeroGame.Persistence.Content;

    /// <summary>
    /// Locates StreamingAssets/Data from either the Unity project root (Unity Test Runner) or a
    /// dotnet test output directory, so the same tests run in both environments.
    /// </summary>
    public static class TestContent
    {
        private static ContentSet _cached;

        public static string DataDirectory
        {
            get
            {
                var candidates = new[] { "Assets/StreamingAssets/Data", "Game/Assets/StreamingAssets/Data" };
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                var start = dir;
                while (dir != null)
                {
                    foreach (var c in candidates)
                    {
                        var path = Path.Combine(dir.FullName, c);
                        if (Directory.Exists(path)) return path;
                    }
                    dir = dir.Parent;
                }
                dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null)
                {
                    foreach (var c in candidates)
                    {
                        var path = Path.Combine(dir.FullName, c);
                        if (Directory.Exists(path)) return path;
                    }
                    dir = dir.Parent;
                }
                throw new DirectoryNotFoundException("Could not find StreamingAssets/Data from " + start.FullName);
            }
        }

        /// <summary>Content is immutable in tests; load once.</summary>
        public static ContentSet Load()
        {
            return _cached ?? (_cached = ContentLoader.Load(DataDirectory));
        }

        public static ServerConfig DefaultConfig()
        {
            return ContentLoader.LoadServerConfig(Path.Combine(DataDirectory, ContentLoader.DefaultServerConfig));
        }

        public static string TempDirectory(string name)
        {
            var path = Path.Combine(Path.GetTempPath(), "herogame-tests", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
