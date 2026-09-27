using System;
using System.Collections.Generic;
using HeroGame.Core.Business;
using HeroGame.Core.Characters;
using HeroGame.Core.Config;
using HeroGame.Core.Crime;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Property;
using HeroGame.Core.Time;
using HeroGame.Core.Weather;
using HeroGame.Core.World;

namespace HeroGame.Core.Simulation
{
    [Serializable]
    public sealed class WellKnownAccounts
    {
        public EntityId Government;
        public EntityId BankOrganization;
        public EntityId PropertyManagement;
        public EntityId External;
        public EntityId Treasury;
        public EntityId BankReserves;
    }

    /// <summary>Simulation bookkeeping that must survive restarts.</summary>
    [Serializable]
    public sealed class SimulationCursor
    {
        public GameDateTime SimulatedUpTo;
        public float DayBadWeatherSum;
        public int DayHours;
        public float DayOutageHours;
        public AnomalyEvent PendingAnomaly;
    }

    /// <summary>Tracks which save chunks changed since the last save (incremental saving, TDD §9.3).</summary>
    public sealed class DirtyTracker
    {
        private readonly HashSet<string> _dirty = new HashSet<string>();

        public void Mark(string chunk) => _dirty.Add(chunk);
        public bool IsDirty(string chunk) => _dirty.Contains(chunk);
        public IReadOnlyCollection<string> Chunks => _dirty;
        public void Clear() => _dirty.Clear();
        public void MarkAll(IEnumerable<string> chunks)
        {
            foreach (var c in chunks) _dirty.Add(c);
        }
    }

    public static class SaveChunks
    {
        public const string Meta = "meta";
        /// <summary>Ledger + ownership + loans: always saved together with the journal sequence.</summary>
        public const string Transactional = "transactional";
        public const string Population = "population";
        public const string Properties = "properties";
        public const string Businesses = "businesses";
        public const string Environment = "environment";
        public const string History = "history";
        public const string CharacterPrefix = "character/";
        public static readonly string[] WorldChunks = { Meta, Transactional, Population, Properties, Businesses, Environment, History };
    }

    /// <summary>
    /// Root object of one persistent world (a server or a Story Mode save). Owns the state and the
    /// services that operate on it. Deliberately not a god-object: each subsystem lives in its own
    /// class; <see cref="World"/> only wires them together (TDD §5.1).
    /// </summary>
    public sealed class World
    {
        public readonly string ServerId;
        public readonly ServerConfig Config;
        public readonly ContentSet Content;
        public readonly ulong Seed;

        public readonly WorldClock Clock;
        public readonly IdAllocator Ids;
        public readonly EventBus Events = new EventBus();
        public readonly DirtyTracker Dirty = new DirtyTracker();

        public readonly Geography Geography = new Geography();
        public readonly Ledger Ledger = new Ledger();
        public readonly OwnershipRegistry Ownership = new OwnershipRegistry();
        public readonly LoanBook Loans = new LoanBook();
        public readonly TransactionProcessor Transactions;
        public readonly TaxPolicy Taxes;
        public readonly PropertyService Properties;

        public readonly Dictionary<EntityId, BusinessRecord> Businesses = new Dictionary<EntityId, BusinessRecord>();
        public readonly BusinessSimulator BusinessSim;

        public readonly OccupationTable Occupations;
        public readonly PopulationRegistry Population = new PopulationRegistry();
        public readonly ScheduleResolver Schedules;
        public readonly SocialSimulator Social;
        public readonly PopulationDirector Director;

        public readonly MacroEconomyState Macro;
        public readonly WeatherSystem Weather;
        public readonly PowerGenerator PowerGenerator;
        public readonly AnomalySystem Anomalies;
        public readonly PowerInteractionResolver Interactions;
        public readonly WantedSystem Wanted;
        public readonly ServerHistory History = new ServerHistory();
        public readonly List<AnomalyEvent> AnomalyLog = new List<AnomalyEvent>();
        public readonly Dictionary<EntityId, ServerCharacter> Characters = new Dictionary<EntityId, ServerCharacter>();

        public WellKnownAccounts Accounts = new WellKnownAccounts();
        public SimulationCursor Cursor = new SimulationCursor();

        /// <summary>Supplies online characters (position, shelter) for anomaly exposure. Set by the server/runtime.</summary>
        public Func<IEnumerable<ExposureCandidate>> OnlineCharacters;

        public World(string serverId, ServerConfig config, ContentSet content, ITransactionJournal journal,
            WorldClock clock = null, IdAllocator ids = null, MacroEconomyState macro = null, WeatherSimState weather = null, long lastJournalSequence = 0)
        {
            ServerId = serverId ?? "local";
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            Seed = (ulong)(uint)config.Identity.WorldSeed | ((ulong)StableHash.Of(ServerId) << 32);
            Clock = clock ?? new WorldClock(GameDateTime.FromCalendar(2030, 5, 6, 8, 0), WorldClock.TimeScaleForDayLength(config.Gameplay.RealMinutesPerGameDay));
            Ids = ids ?? new IdAllocator();
            Macro = macro ?? new MacroEconomyState();

            Transactions = new TransactionProcessor(Ledger, Ownership, journal, lastJournalSequence);
            Taxes = new TaxPolicy(config.Economy);
            Properties = new PropertyService(Ownership, Transactions, Taxes);
            BusinessSim = new BusinessSimulator(Transactions, Taxes, content.BusinessTemplates);

            Occupations = new OccupationTable(content.Occupations);
            Schedules = new ScheduleResolver(Seed, Occupations, Geography);
            Social = new SocialSimulator(Seed, Ids, content.Names);
            Director = new PopulationDirector(Population, Geography, Schedules);

            Weather = new WeatherSystem(Seed, weather);
            if (!config.Gameplay.DynamicWeather) Weather.State.FixedWeather = string.IsNullOrEmpty(config.Gameplay.FixedWeather) ? "Clear" : config.Gameplay.FixedWeather;
            PowerGenerator = new PowerGenerator(content.PowerArchetypes);
            Anomalies = new AnomalySystem(Seed, PowerGenerator, content.AnomalyCauses, new AnomalySettings
            {
                FrequencyMultiplier = config.Powers.PowersEnabled ? config.Powers.AnomalyFrequencyMultiplier : 0f,
                ManifestationMultiplier = config.Powers.ManifestationMultiplier,
                MultiplePowerMultiplier = config.Powers.MultiplePowerMultiplier,
                MaxPowers = config.Powers.MaxPowersPerCharacter,
            });
            Interactions = new PowerInteractionResolver(content.InteractionRules);
            Wanted = new WantedSystem(new WantedSettings { ResponseMultiplier = config.Gameplay.PoliceResponseMultiplier });

            Ownership.Transferred += (asset, from, to) => Dirty.Mark(SaveChunks.Transactional);
            Transactions.Committed += tx => Dirty.Mark(SaveChunks.Transactional);
            History.Recorded += r => Dirty.Mark(SaveChunks.History);
        }

        public long Today => Clock.Now.DayIndex;

        public LifeSimContext CreateLifeContext()
        {
            return new LifeSimContext
            {
                WorldSeed = Seed,
                Occupations = Occupations,
                Geography = Geography,
                Macro = Macro,
                IncomeTaxRate = Config.Economy.IncomeTaxRate,
                WageMultiplier = Config.Economy.WageMultiplier,
                CostOfLivingMultiplier = Config.Economy.Difficulty,
                WorkplacesByKind = LifeSimContext.IndexWorkplaces(Geography),
            };
        }

        /// <summary>Creates this server's version of an account's character (GDD §5).</summary>
        public ServerCharacter CreateCharacter(AccountProfile account, WorldPosition spawn)
        {
            var character = new ServerCharacter
            {
                CharacterId = Ids.Next(EntityKind.Character),
                AccountId = account.AccountId,
                ServerId = ServerId,
                CreatedDay = Today,
                LastPosition = spawn,
            };
            character.CheckingAccount = Ids.Next(EntityKind.LedgerAccount);
            Ledger.Open(character.CheckingAccount, character.CharacterId, LedgerAccountKind.PersonalChecking, account.Character.FullName + " checking");
            character.SavingsAccount = Ids.Next(EntityKind.LedgerAccount);
            Ledger.Open(character.SavingsAccount, character.CharacterId, LedgerAccountKind.PersonalSavings, account.Character.FullName + " savings");
            character.Record.Subject = character.CharacterId;
            Characters[character.CharacterId] = character;

            if (Config.Economy.StartingCashCents > 0)
            {
                Transactions.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Player,
                    IdempotencyKey = "start-funds:" + character.CharacterId,
                    Initiator = character.CharacterId,
                    Timestamp = Clock.Now,
                    Description = "Starting funds",
                    Money = LedgerTransaction.Transfer(Accounts.External, character.CheckingAccount, new Money(Config.Economy.StartingCashCents), TransactionReason.StartingFunds),
                });
            }
            Dirty.Mark(SaveChunks.CharacterPrefix + character.CharacterId);
            return character;
        }

        /// <summary>Admin/debug money grant. Always journaled and attributable (never silent currency creation).</summary>
        public OpResult AdminGrant(EntityId account, Money amount, string adminAccountId, string reason)
        {
            return Transactions.Execute(new WorldTransaction
            {
                Source = TransactionSource.Admin,
                Timestamp = Clock.Now,
                Description = "Admin grant by " + adminAccountId + ": " + reason,
                Money = LedgerTransaction.Transfer(Accounts.External, account, amount, TransactionReason.AdminGrant, reason),
            });
        }
    }
}
