using System;
using System.IO;
using HeroGame.Core.Characters;
using HeroGame.Core.Foundation;
using HeroGame.Core.World;
using HeroGame.Persistence.Content;
using HeroGame.Persistence.Saves;
using HeroGame.Core.Simulation;
using HeroGame.Core.Story;
using UnityEngine;

namespace HeroGame.Runtime.Bootstrap
{
    /// <summary>
    /// Composition root for a gameplay scene. Loads content, opens the world session, resolves the
    /// local character and registers services. Everything else receives what it needs from here.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class GameBootstrap : MonoBehaviour
    {
        [Tooltip("Story uses a local slot; LocalServer hosts a private persistent world on this machine.")]
        public SessionMode Mode = SessionMode.LocalServer;
        public string ServerId = "local-dev";
        public string SaveSlot = "local-dev";
        public Transform PlayerSpawn;
        [Tooltip("City layout for NEW worlds (StreamingAssets/Data). layout_vertical_slice.json is the 3-district slice; " +
                 "layout_port_arden.json is the full 19-district metro. Existing saves always reopen on their own layout.")]
        public string LayoutFile = ContentLoader.DefaultLayout;
        [Tooltip("Real seconds between autosaves.")]
        public float AutosaveSeconds = 300f;

        public GameSession Session { get; private set; }
        public ContentSet Content { get; private set; }
        public AccountProfile Account { get; private set; }

        public static GameBootstrap Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            if (LaunchRequest.TryConsume(out var mode, out var serverId, out var slot))
            {
                Mode = mode;
                if (!string.IsNullOrEmpty(serverId)) ServerId = serverId;
                if (!string.IsNullOrEmpty(slot)) SaveSlot = slot;
            }

            var folder = Mode == SessionMode.Story ? Path.Combine(GameSession.SaveRoot, "story", SaveSlot) : Path.Combine(GameSession.SaveRoot, "servers", SaveSlot);
            var layout = new WorldSaveSystem(folder).RecordedLayoutFile() ?? (string.IsNullOrEmpty(LayoutFile) ? ContentLoader.DefaultLayout : LayoutFile);
            Content = ContentLoader.Load(GameSession.DataDirectory, layout);
            var report = ContentLoader.Validate(Content);
            foreach (var m in report.Messages) Debug.LogWarning("[Content] " + m);
            if (report.HasErrors) throw new InvalidDataException("Content validation failed; see warnings above.");

            Account = LoadOrCreateAccount();
            StoryDefinition story = null;
            Core.Time.GameDateTime? start = null;
            if (Mode == SessionMode.Story)
            {
                story = ContentLoader.LoadStory(GameSession.DataDirectory);
                var storyReport = ContentLoader.ValidateStory(story, Content);
                foreach (var m in storyReport.Messages) Debug.LogWarning("[Story] " + m);
                if (storyReport.HasErrors) throw new InvalidDataException("Story validation failed; see warnings above.");
                start = ParseStart(story.Start);
            }
            Session = GameSession.OpenOrCreate(Mode, ServerId, folder, Content, start);
            Session.AutosaveIntervalSeconds = AutosaveSeconds;
            Session.Saved += r => Debug.Log("[Save] generation " + r.Generation + ": " + r.ChunksWritten + " chunks in " + r.Milliseconds.ToString("0") + " ms");

            var spawn = PlayerSpawn != null ? PlayerSpawn.position : Vector3.zero;
            if (story != null)
            {
                // New story: cast the characters and start Part One. Loaded story: resume where the save left off.
                if (Session.World.Story == null) Session.UseStory(StoryService.Begin(Session.World, story, Account));
                else Session.UseStory(StoryService.Attach(Session.World, story, Session.World.Story, Session.EnsureCharacter(Account, spawn)));
            }
            else Session.EnsureCharacter(Account, spawn);

            ServiceRegistry.Register(Session);
            ServiceRegistry.Register(Content);
            ServiceRegistry.Register(Account);
            Debug.Log("[Bootstrap] " + Session.World.Config.Identity.CityName + " · " + Session.World.Clock.Now + " · " + Session.World.Population.Count + " residents");
        }

        private void Update()
        {
            Session?.Tick(Time.unscaledDeltaTime * Mathf.Max(0f, Time.timeScale));
        }

        private void OnApplicationQuit()
        {
            if (Session == null) return;
            Session.Save();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            ServiceRegistry.Unregister(Session);
            Session?.Dispose();
            Instance = null;
        }

        private static Core.Time.GameDateTime ParseStart(string text)
        {
            var p = text.Split(' ', '-', ':');
            return Core.Time.GameDateTime.FromCalendar(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]));
        }

        public static AccountStore Accounts => new AccountStore(Path.Combine(GameSession.SaveRoot, "accounts"));

        private static AccountProfile LoadOrCreateAccount()
        {
            var store = Accounts;
            var profile = store.Load("local");
            if (profile != null) return profile;
            // Offline default until the character creator has run. Online, the account service issues ids.
            profile = new AccountProfile
            {
                AccountId = EntityId.Create(EntityKind.UserAccount, 1),
                DisplayName = Environment.UserName,
                Character = new CharacterIdentity { FirstName = "Alex", LastName = "Rivera", Age = 21 },
            };
            store.Save("local", profile);
            return profile;
        }
    }
}
