using System;
using System.IO;
using HeroGame.Core.Characters;
using HeroGame.Core.Foundation;
using HeroGame.Core.World;
using HeroGame.Persistence.Content;
using HeroGame.Persistence.Saves;
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

            Content = ContentLoader.Load(GameSession.DataDirectory);
            var report = ContentLoader.Validate(Content);
            foreach (var m in report.Messages) Debug.LogWarning("[Content] " + m);
            if (report.HasErrors) throw new InvalidDataException("Content validation failed; see warnings above.");

            Account = LoadOrCreateAccount();
            var folder = Mode == SessionMode.Story ? Path.Combine(GameSession.SaveRoot, "story", SaveSlot) : Path.Combine(GameSession.SaveRoot, "servers", SaveSlot);
            Session = GameSession.OpenOrCreate(Mode, ServerId, folder, Content);
            Session.AutosaveIntervalSeconds = AutosaveSeconds;
            Session.Saved += r => Debug.Log("[Save] generation " + r.Generation + ": " + r.ChunksWritten + " chunks in " + r.Milliseconds.ToString("0") + " ms");

            var spawn = PlayerSpawn != null ? PlayerSpawn.position : Vector3.zero;
            Session.EnsureCharacter(Account, spawn);

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
