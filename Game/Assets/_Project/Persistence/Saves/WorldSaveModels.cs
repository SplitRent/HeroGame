using System.Collections.Generic;
using HeroGame.Core.Business;
using HeroGame.Core.Config;
using HeroGame.Core.Economy;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Property;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using HeroGame.Core.Weather;
using HeroGame.Core.World;

namespace HeroGame.Persistence.Saves
{
    /// <summary>
    /// The commit record of a snapshot. Written last, atomically. It names the generation of every
    /// chunk file, so a crash mid-save leaves the previous snapshot fully intact (TDD §9.3).
    /// </summary>
    public sealed class SaveManifest
    {
        public int SchemaVersion = WorldSaveSystem.CurrentSchema;
        public string GameVersion = "";
        public string ServerId = "";
        public long SavedAtUnix;
        public long Generation;
        /// <summary>Last journal sequence folded into this snapshot (the journal is compacted only up to the previous one).</summary>
        public long JournalSequence;
        public Dictionary<string, long> ChunkGenerations = new Dictionary<string, long>();
    }

    public sealed class MetaChunk
    {
        public ServerConfig Config;
        public GameDateTime Now;
        public double TimeScale;
        public ulong[] IdsLastIssued;
        public WellKnownAccounts Accounts;
        public SimulationCursor Cursor;
        public MacroEconomyState Macro;
        public List<string> CharacterIds = new List<string>();
    }

    public sealed class TransactionalChunk
    {
        public long LastSequence;
        public List<LedgerAccount> Accounts = new List<LedgerAccount>();
        public List<OwnershipEntry> Ownership = new List<OwnershipEntry>();
        public List<Loan> Loans = new List<Loan>();
        public List<InsurancePolicy> Policies = new List<InsurancePolicy>();
    }

    /// <summary>Households plus the list of NPC shards that exist.</summary>
    public sealed class PopulationChunk
    {
        public List<Household> Households = new List<Household>();
        public List<int> Shards = new List<int>();
        /// <summary>Only used by schema 1 saves (all NPCs in one chunk).</summary>
        public List<NpcRecord> Npcs = new List<NpcRecord>();
    }

    public sealed class PopulationShardChunk
    {
        public int Shard;
        public List<NpcRecord> Npcs = new List<NpcRecord>();
    }

    public sealed class PropertiesChunk
    {
        public List<PropertyRecord> Properties = new List<PropertyRecord>();
    }

    public sealed class BusinessesChunk
    {
        public List<BusinessRecord> Businesses = new List<BusinessRecord>();
    }

    public sealed class EnvironmentChunk
    {
        public List<District> Districts = new List<District>();
        public List<Place> Places = new List<Place>();
        public WeatherSimState Weather;
        public List<AnomalyEvent> AnomalyLog = new List<AnomalyEvent>();
    }

    public sealed class CivicChunk
    {
        public Core.Civic.CivicState Civic;
        public Core.Civic.DisasterState Disasters;
    }

    public sealed class VehiclesChunk
    {
        public List<Core.Vehicles.VehicleRecord> Vehicles = new List<Core.Vehicles.VehicleRecord>();
    }

    public sealed class HistoryChunk
    {
        public List<HistoryRecord> Major = new List<HistoryRecord>();
        public List<HistoryRecord> Recent = new List<HistoryRecord>();
    }
}
