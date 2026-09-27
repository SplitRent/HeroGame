using System;
using System.Collections.Generic;
using HeroGame.Core.Building;
using HeroGame.Core.Business;
using HeroGame.Core.Characters;
using HeroGame.Core.Civic;
using HeroGame.Core.Config;
using HeroGame.Core.Crime;
using HeroGame.Core.Economy;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Phone;
using PhoneCategory = HeroGame.Core.Phone.MessageCategory;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Property;
using HeroGame.Core.Social;
using HeroGame.Core.Time;
using HeroGame.Core.Traffic;
using HeroGame.Core.Vehicles;
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
        public EntityId PropertyManagementAccount;
        /// <summary>Contractors, suppliers and other businesses outside the simulated area.</summary>
        public EntityId Contractors;
        public EntityId Insurer;
        public EntityId InsurerAccount;
        public EntityId Hospital;
        public EntityId HospitalAccount;
    }

    /// <summary>Simulation bookkeeping that must survive restarts.</summary>
    [Serializable]
    public sealed class SimulationCursor
    {
        public GameDateTime SimulatedUpTo;
        public float DayBadWeatherSum;
        public int DayHours;
        public float DayOutageHours;
        public int DayPropertiesDamaged;
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
        public const string Justice = "justice";
        public const string Emergency = "emergency";
        /// <summary>Story Mode progress (only present in story saves).</summary>
        public const string Story = "story";
        public const string Environment = "environment";
        public const string History = "history";
        public const string Vehicles = "vehicles";
        /// <summary>City government, elections and disasters.</summary>
        public const string Civic = "civic";
        /// <summary>Ripple posts and follows.</summary>
        public const string Social = "social";
        public const string CharacterPrefix = "character/";
        /// <summary>NPCs are saved in fixed-size shards by id so large populations serialize in parallel.</summary>
        public const string PopulationShardPrefix = "population/";
        public const int NpcsPerShard = 2048;

        public static string PopulationShard(int index) => PopulationShardPrefix + index;
        public static int ShardOf(EntityId npc) => (int)((npc.Sequence - 1) / NpcsPerShard);
        public static readonly string[] WorldChunks = { Meta, Transactional, Population, Properties, Businesses, Environment, History, Vehicles, Justice, Emergency, Civic, Social };
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
        /// <summary>Player ↔ NPC conversations and memory (distinct from <see cref="Interactions"/>, the power/material rules).</summary>
        public readonly InteractionService Conversations;
        public readonly VehicleService Vehicles;
        public readonly ConstructionService Construction;
        public readonly RentalService Rentals;
        public RoadNetwork Roads { get; private set; }
        public TrafficModel Traffic { get; private set; }
        public readonly PhoneService Phone;
        public readonly InsuranceBook Insurance = new InsuranceBook();
        public readonly FinanceService Finance;
        public readonly BusinessOperations BusinessOps;
        /// <summary>Incidents, evidence and court cases (persisted in the justice chunk).</summary>
        public JusticeState Justice = new JusticeState();
        public readonly CrimeService Crimes;
        public readonly JusticeService Courts;
        /// <summary>Emergency incidents and unit assignments (persisted in the emergency chunk).</summary>
        public EmergencyState Emergency = new EmergencyState();
        /// <summary>Story Mode progress; null on player servers.</summary>
        public Story.StoryState Story;
        public readonly EmergencyDispatch Dispatch;
        /// <summary>Executes power uses against the world (effects, collateral, witnesses, crimes).</summary>
        public readonly PowerService PowerUse;
        /// <summary>Budget, ordinances, opinion, officeholders and elections (persisted in the civic chunk).</summary>
        public CivicState Civic = new CivicState();
        /// <summary>Active and recent local disasters (persisted in the civic chunk).</summary>
        public DisasterState Disasters = new DisasterState();
        /// <summary>Ripple posts and follows (persisted in the social chunk).</summary>
        public RippleState Ripple = new RippleState();
        public readonly CivicService Government;
        public readonly RippleService Feed;
        public readonly CalendarService Calendar;
        public readonly RadioService Radio;
        /// <summary>Bumped when the set of NPC-hireable workplaces changes (player takes over staffing, etc.).</summary>
        public int WorkplaceVersion;

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

            Conversations = new InteractionService(this, content.Barks);
            Vehicles = new VehicleService(content.VehicleModels, content.VehicleMods, Transactions, Ownership, Taxes, Ids, Seed);
            RebuildRoads();
            Construction = new ConstructionService(new BuildingValidator(content.Furniture), Transactions, Ownership, content.BusinessRequirements);
            Rentals = new RentalService(Properties, Transactions, Ownership, Taxes);
            Rentals.Event += OnRentalEvent;
            Phone = new PhoneService(this);
            Finance = new FinanceService(this);
            BusinessOps = new BusinessOperations(this);
            Crimes = new CrimeService(this);
            Courts = new JusticeService(this);
            Dispatch = new EmergencyDispatch(this);
            PowerUse = new PowerService(this);
            Government = new CivicService(this);
            Feed = new RippleService(this);
            Calendar = new CalendarService(this);
            Radio = new RadioService(this);
            Transactions.Applied += RegisterRecords;

            Ownership.Transferred += (asset, from, to) => Dirty.Mark(SaveChunks.Transactional);
            Transactions.Committed += tx => Dirty.Mark(SaveChunks.Transactional);
            History.Recorded += r => Dirty.Mark(SaveChunks.History);
            Wanted.LevelChanged += s => Dirty.Mark(SaveChunks.Justice);
        }

        public long Today => Clock.Now.DayIndex;

        /// <summary>Registers records that arrived with a transaction (live or during journal replay).</summary>
        private void RegisterRecords(WorldTransaction tx)
        {
            var r = tx.Records;
            if (r == null) return;
            if (r.Loan != null) Loans.Restore(r.Loan);
            if (r.Policy != null) Insurance.Restore(r.Policy);
            if (r.Business != null)
            {
                Businesses[r.Business.Id] = r.Business;
                BusinessOps.OnRegistered(r.Business);
                Dirty.Mark(SaveChunks.Businesses);
            }
        }

        /// <summary>
        /// Opens institutions added after a world was first generated (older saves) and tops up their capital.
        /// Idempotent; called after generation and after every load.
        /// </summary>
        /// <summary>Loads the justice chunk and re-files its evidence with the police.</summary>
        public void RestoreJustice(JusticeState state)
        {
            Justice = state ?? new JusticeState();
            foreach (var e in Justice.Evidence) Wanted.AddEvidence(e);
            if (Justice.Wanted != null) foreach (var s in Justice.Wanted) Wanted.Restore(s);
        }

        public void EnsureInstitutions()
        {
            BusinessOps.Reconcile();
            if (!Accounts.Hospital.IsValid) Accounts.Hospital = Ids.Next(EntityKind.Organization);
            if (!Accounts.HospitalAccount.IsValid || !Ledger.Exists(Accounts.HospitalAccount))
            {
                if (!Accounts.HospitalAccount.IsValid) Accounts.HospitalAccount = Ids.Next(EntityKind.LedgerAccount);
                Ledger.Open(Accounts.HospitalAccount, Accounts.Hospital, LedgerAccountKind.Organization, EmergencyDispatch.HospitalName);
            }
            Dispatch.EnsureUnits();
            Government.OnLoaded();
            var a = Accounts;
            if (!a.Insurer.IsValid) a.Insurer = Ids.Next(EntityKind.Organization);
            if (!a.InsurerAccount.IsValid || !Ledger.Exists(a.InsurerAccount))
            {
                if (!a.InsurerAccount.IsValid) a.InsurerAccount = Ids.Next(EntityKind.LedgerAccount);
                Ledger.Open(a.InsurerAccount, a.Insurer, LedgerAccountKind.Organization, FinanceService.InsurerName + " reserves");
                Transactions.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation,
                    Timestamp = Clock.Now,
                    Description = "Insurer capitalisation",
                    Money = LedgerTransaction.Transfer(Accounts.External, a.InsurerAccount, Money.FromDollars(200000000L), TransactionReason.WorldGeneration, "Reserves"),
                });
            }
        }

        /// <summary>The checking account of a player character, or None.</summary>
        public EntityId CheckingAccountOf(EntityId character) => Characters.TryGetValue(character, out var c) ? c.CheckingAccount : EntityId.None;

        /// <summary>The ledger account that receives money for an owner (character, business owner org, management…).</summary>
        public EntityId AccountFor(EntityId owner)
        {
            if (!owner.IsValid) return Accounts.Treasury;
            if (Characters.TryGetValue(owner, out var c)) return c.CheckingAccount;
            if (owner == Accounts.PropertyManagement && Accounts.PropertyManagementAccount.IsValid) return Accounts.PropertyManagementAccount;
            if (owner == Accounts.Government) return Accounts.Treasury;
            if (owner == Accounts.BankOrganization) return Accounts.BankReserves;
            foreach (var a in Ledger.Accounts) if (a.Owner == owner && a.Kind != LedgerAccountKind.External) return a.Id;
            return Accounts.Treasury;
        }

        /// <summary>
        /// Applies a build session to a property the character owns, charging the contractor at today's price
        /// level. The layout lives in the properties chunk, so callers that journal only money must save that
        /// chunk promptly (see GameSession.Build) to keep layout and payment consistent across a crash.
        /// </summary>
        public OpResult Build(PropertyRecord property, IReadOnlyList<BuildOp> ops, ServerCharacter builder, string idempotencyKey)
        {
            if (property == null || builder == null) return OpResult.Fail("Nothing to build.");
            if (property.Layout == null) return OpResult.Fail("This property has no editable structure.");
            return Construction.Commit(property, property.Layout, ops, builder.CharacterId, builder.CheckingAccount, Accounts.Contractors, Clock.Now,
                Macro.PriceLevel, idempotencyKey, l =>
                {
                    property.Layout = l;
                    Dirty.Mark(SaveChunks.Properties);
                });
        }

        private void OnRentalEvent(RentalEvent e)
        {
            Dirty.Mark(SaveChunks.Properties);
            if (Characters.TryGetValue(e.Party, out var c) && e.Kind != RentalEventKind.RentPaid && e.Kind != RentalEventKind.TaxPaid)
                Phone.Send(c, Accounts.PropertyManagement, e.Kind == RentalEventKind.TaxArrears || e.Kind == RentalEventKind.TaxSale ? Config.Identity.GovernmentName : "Landlord",
                    Phone_Category(e.Kind), e.Detail);
            if (e.Kind == RentalEventKind.TaxSale || e.Kind == RentalEventKind.Evicted)
                History.Record(Today, HistoryCategory.Economy, 1, e.Detail, "", EntityId.None, e.Property);
        }

        private static PhoneCategory Phone_Category(RentalEventKind kind) => kind == RentalEventKind.TaxArrears || kind == RentalEventKind.TaxSale ? PhoneCategory.Server : PhoneCategory.Personal;

        /// <summary>Rebuilds the road graph from layout data (after load or layout edits).</summary>
        public void RebuildRoads()
        {
            Roads = RoadNetwork.Build(Content.Layout.Roads);
            Traffic = new TrafficModel(Roads, Seed, Config.Gameplay.TrafficDensity);
        }

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
