using System;
using System.Collections.Generic;
using System.IO;
using HeroGame.Core.Building;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Property;
using HeroGame.Core.Simulation;
using HeroGame.Core.World;
using HeroGame.Persistence.Content;
using HeroGame.Persistence.Saves;
using HeroGame.Persistence.Storage;
using UnityEngine;

namespace HeroGame.Runtime.Bootstrap
{
    public enum SessionMode
    {
        /// <summary>Single-player canonical world, saved to story slots.</summary>
        Story,
        /// <summary>Local listen-server world (development / private play). Dedicated servers use the headless host.</summary>
        LocalServer,
    }

    /// <summary>
    /// One running world on this machine: owns the <see cref="World"/>, its simulation, save
    /// system and journal. Pure C# (no MonoBehaviour) so its lifetime is explicit;
    /// <see cref="GameBootstrap"/> ticks it.
    /// </summary>
    public sealed class GameSession : IDisposable
    {
        public readonly SessionMode Mode;
        public readonly World World;
        public readonly WorldSimulation Simulation;
        public readonly WorldSaveSystem Saves;
        public ServerCharacter LocalCharacter { get; private set; }

        private readonly FileTransactionJournal _journal;
        private float _autosaveTimer;

        public float AutosaveIntervalSeconds = 300f;
        public event Action<SaveResult> Saved;

        private GameSession(SessionMode mode, World world, WorldSaveSystem saves, FileTransactionJournal journal)
        {
            Mode = mode;
            World = world;
            Saves = saves;
            _journal = journal;
            Simulation = new WorldSimulation(world);
        }

        public static string DataDirectory => Path.Combine(Application.streamingAssetsPath, "Data");

        public static string SaveRoot => Path.Combine(Application.persistentDataPath, "Saves");

        /// <summary>Opens (or creates) a world in <paramref name="saveDirectory"/>.</summary>
        public static GameSession OpenOrCreate(SessionMode mode, string serverId, string saveDirectory, ContentSet content, Core.Time.GameDateTime? start = null)
        {
            var saves = new WorldSaveSystem(saveDirectory);
            var journal = saves.OpenJournal();
            World world;
            if (saves.Exists)
            {
                var load = saves.Load(content, journal);
                foreach (var m in load.Report.Messages) Debug.LogWarning("[Save] " + m);
                if (load.Report.HasErrors)
                {
                    journal.Dispose();
                    throw new InvalidDataException("Save failed integrity checks: " + load.Report);
                }
                if (load.JournalEntriesReplayed > 0) Debug.Log("[Save] Recovered " + load.JournalEntriesReplayed + " transaction(s) from the journal.");
                world = load.World;
            }
            else
            {
                var config = ContentLoader.LoadServerConfig(Path.Combine(DataDirectory, ContentLoader.DefaultServerConfig));
                world = WorldGenerator.Create(serverId, config, content, journal, start);
                saves.Save(world, full: true);
            }
            return new GameSession(mode, world, saves, journal);
        }

        /// <summary>Story Mode director when this session is a story slot.</summary>
        public StoryService Story { get; private set; }

        /// <summary>Story Mode gates some actions by chapter (a sixteen-year-old cannot sign a mortgage).</summary>
        public OpResult CheckAllowed(string action) =>
            Story == null || Story.Allows(action) ? OpResult.Ok() : OpResult.Fail("Not yet — that comes later in your story.");

        /// <summary>Makes the story's protagonist the local character and lets story time effects run the simulation.</summary>
        public void UseStory(StoryService story)
        {
            Story = story;
            story.Simulation = Simulation;
            LocalCharacter = story.Player;
        }

        /// <summary>Finds or creates this server's version of the local account's character.</summary>
        public ServerCharacter EnsureCharacter(AccountProfile account, Vector3 spawn)
        {
            foreach (var c in World.Characters.Values)
            {
                if (c.AccountId != account.AccountId) continue;
                LocalCharacter = c;
                return c;
            }
            LocalCharacter = World.CreateCharacter(account, spawn.ToWorld());
            return LocalCharacter;
        }

        public void Tick(float realDeltaSeconds)
        {
            World.Clock.AdvanceReal(realDeltaSeconds);
            if (World.StreetCrime.IsPresent == null) World.StreetCrime.IsPresent = c => c == LocalCharacter && !Runtime.Online.NetworkSession.Connected;
            Simulation.Update();
            if (LocalCharacter != null) World.StreetCrime.Update(LocalCharacter, LocalCharacter.LastPosition);
            World.Events.Flush();
            _autosaveTimer += realDeltaSeconds;
            if (_autosaveTimer >= AutosaveIntervalSeconds && _autosave == null)
            {
                _autosaveTimer = 0f;
                // Only serialization happens on this frame; disk writes run on a background thread.
                _autosave = Saves.SaveInBackground(World);
            }
            if (_autosave != null && _autosave.IsCompleted)
            {
                if (_autosave.IsFaulted) Debug.LogWarning("[Save] autosave failed, will retry: " + _autosave.Exception?.GetBaseException().Message);
                else
                {
                    Saves.CompactJournal(World, _journal);
                    Saved?.Invoke(_autosave.Result);
                }
                _autosave = null;
            }
        }

        private System.Threading.Tasks.Task<SaveResult> _autosave;

        /// <summary>Synchronous save (menus, quitting, story checkpoints); waits for any autosave still writing.</summary>
        public SaveResult Save()
        {
            var result = Saves.Save(World);
            _autosave = null;
            Saves.CompactJournal(World, _journal);
            Saved?.Invoke(result);
            return result;
        }

        private long _requestCounter;

        /// <summary>A fresh idempotency key for one player request (network clients will send their own).</summary>
        public string NextRequestKey(string operation) =>
            operation + ":" + (LocalCharacter != null ? LocalCharacter.CharacterId.ToString() : "-") + ":" + World.Clock.Now.TotalSeconds + ":" + (++_requestCounter);

        /// <summary>Player-facing purchase with an idempotency key, the way network requests will call it.</summary>
        public OpResult BuyProperty(EntityId property)
        {
            if (LocalCharacter == null) return OpResult.Fail("No character.");
            var allowed = CheckAllowed("property.buy");
            if (!allowed.Success) return allowed;
            var key = "buy:" + LocalCharacter.CharacterId + ":" + property + ":" + World.Clock.Now.TotalSeconds;
            return World.Properties.Purchase(property, LocalCharacter.CharacterId, LocalCharacter.CheckingAccount,
                SellerAccountFor(property), World.Accounts.Treasury, World.Clock.Now, key);
        }

        /// <summary>Commits a build session on an owned property and saves at once so the layout is never behind its payment.</summary>
        public OpResult Build(PropertyRecord property, IReadOnlyList<BuildOp> ops)
        {
            if (LocalCharacter == null) return OpResult.Fail("No character.");
            var key = "build:" + LocalCharacter.CharacterId + ":" + property.Id + ":" + World.Clock.Now.TotalSeconds + ":" + ops.Count;
            var result = World.Build(property, ops, LocalCharacter, key);
            if (result.Success) Save();
            return result;
        }

        private EntityId SellerAccountFor(EntityId property)
        {
            var owner = World.Ownership.OwnerOf(property);
            if (!owner.IsValid) return World.Accounts.Treasury;
            foreach (var c in World.Characters.Values) if (c.CharacterId == owner) return c.CheckingAccount;
            foreach (var a in World.Ledger.Accounts) if (a.Owner == owner && a.Kind != LedgerAccountKind.External) return a.Id;
            return World.Accounts.Treasury;
        }

        public void Dispose()
        {
            _journal?.Dispose();
        }
    }
}
