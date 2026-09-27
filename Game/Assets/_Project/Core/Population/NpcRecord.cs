using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Powers;

namespace HeroGame.Core.Population
{
    public enum Sex
    {
        Female,
        Male,
    }

    public enum GenderPresentation
    {
        Feminine,
        Masculine,
        Androgynous,
    }

    public enum EducationLevel
    {
        None,
        HighSchool,
        Vocational,
        Bachelors,
        Masters,
        Doctorate,
    }

    public enum EmploymentStatus
    {
        Child,
        Student,
        Employed,
        Unemployed,
        Retired,
    }

    public enum RelationshipType
    {
        Acquaintance,
        Parent,
        Child,
        Sibling,
        Spouse,
        Partner,
        ExPartner,
        Friend,
        Coworker,
        Neighbor,
        Employer,
        Employee,
        Rival,
    }

    /// <summary>How much simulation an NPC currently receives (TDD §7.2).</summary>
    public enum SimulationTier
    {
        /// <summary>Statistical/daily simulation only. Location derived from schedule on demand.</summary>
        Background,
        /// <summary>Near a player: lightweight agent (crowd LOD), schedule-driven movement.</summary>
        Nearby,
        /// <summary>Close to a player: full AI, animation, perception, dialogue.</summary>
        Full,
    }

    public enum ActivityKind
    {
        Sleeping,
        AtHome,
        Commuting,
        Working,
        AtSchool,
        Shopping,
        Eating,
        Leisure,
        Exercising,
        Socializing,
        Worship,
        Hospitalized,
        InCustody,
        Deceased,
    }

    [Serializable]
    public struct Personality
    {
        // Big Five, each 0..1.
        public float Openness;
        public float Conscientiousness;
        public float Extraversion;
        public float Agreeableness;
        public float Neuroticism;
    }

    [Serializable]
    public sealed class Relationship
    {
        public EntityId Other;
        public RelationshipType Type;
        /// <summary>-1 (hostile) .. 1 (close).</summary>
        public float Affinity;
        /// <summary>0 (strangers) .. 1 (know each other intimately).</summary>
        public float Familiarity;
        public long SinceDay;

        public Relationship Copy() => (Relationship)MemberwiseClone();
    }

    [Flags]
    public enum MemoryFlags
    {
        None = 0,
        WitnessedCrime = 1,
        VictimOfCrime = 2,
        ReceivedFavor = 4,
        ReceivedGift = 8,
        HadConflict = 16,
        WasRescued = 32,
        SawPowers = 64,
        SuspectsSecretIdentity = 128,
        Customer = 256,
        Employer = 512,
    }

    /// <summary>
    /// Summarised memory of a player character (GDD §96). Never raw history: a bounded
    /// set of scalars and flags that dialogue and behaviour can query.
    /// </summary>
    [Serializable]
    public sealed class CharacterMemory
    {
        public EntityId Character;
        public float Affinity;
        public float Trust;
        public float Fear;
        public int Interactions;
        public long FirstMetDay;
        public long LastSeenDay;
        public MemoryFlags Flags;
        public string LastTopic = "";

        public CharacterMemory Copy() => (CharacterMemory)MemberwiseClone();
    }

    /// <summary>One line of a person's life story. Immutable once recorded (save snapshots share instances).</summary>
    [Serializable]
    public sealed class LifeEvent
    {
        public long Day;
        public string Kind = "";
        public string Summary = "";
    }

    /// <summary>
    /// Persistent identity and state of one simulated person (GDD §12). NPCs never regenerate:
    /// the same record is materialised whenever the person is relevant to a player.
    /// </summary>
    [Serializable]
    public sealed class NpcRecord
    {
        public const int MaxHistory = 24;
        public const int MaxMemories = 32;

        public EntityId Id;
        public string FirstName = "";
        public string LastName = "";
        public long BirthDay;
        public Sex Sex;
        public GenderPresentation Presentation;
        public ulong AppearanceSeed;
        public float HeightCm = 170f;
        public int SkinTone;
        public Personality Personality;
        public List<string> Interests = new List<string>();

        public EntityId Household;
        public EntityId Home;
        public EntityId Workplace;
        public EntityId School;
        public string OccupationId = "";
        public long AnnualSalaryCents;
        public EmploymentStatus Employment;
        public EducationLevel Education;
        public long JobStartDay;
        public float JobPerformance = 0.5f;
        public List<EntityId> FavoritePlaces = new List<EntityId>();
        public EntityId Vehicle;

        public long SavingsCents;
        public long DebtCents;

        public float Health = 1f;
        public bool Alive = true;
        public long DeathDay;
        /// <summary>0..1 inclination toward crime; modulated by finances and district.</summary>
        public float CriminalPropensity;
        public int Arrests;
        public bool HasCriminalRecord;
        /// <summary>Story/quest-tagged NPCs are never culled or randomised.</summary>
        public bool IsImportant;
        /// <summary>Null for the vast majority of people. NPCs can be exposed to anomalies too.</summary>
        public CharacterPowers Powers;

        public List<Relationship> Relationships = new List<Relationship>();
        public List<CharacterMemory> Memories = new List<CharacterMemory>();
        public List<LifeEvent> History = new List<LifeEvent>();

        // Dynamic state.
        public SimulationTier Tier;
        public long LastSimulatedDay = long.MinValue;
        public ActivityKind CurrentActivity;
        public EntityId CurrentPlace;
        /// <summary>Non-zero while an override (hospital, custody) applies; day index when it ends.</summary>
        public long OverrideUntilDay;
        public ActivityKind OverrideActivity;
        public EntityId OverridePlace;

        public string FullName => FirstName + " " + LastName;

        /// <summary>
        /// A copy that shares nothing mutable with this record, so a save can serialize it on another thread while the
        /// simulation keeps changing the original. Far cheaper than serializing: plain fields are copied in one block and
        /// only the small nested collections are duplicated. Immutable parts (life events, power definitions) are shared.
        /// Any new mutable reference field must be copied here (SaveSnapshotTests checks this).
        /// </summary>
        public NpcRecord SnapshotCopy()
        {
            // Snapshots are read-only (they are only serialized), so every empty collection can be one shared instance.
            var c = (NpcRecord)MemberwiseClone();
            c.Interests = Interests.Count == 0 ? NoStrings : new List<string>(Interests);
            c.FavoritePlaces = FavoritePlaces.Count == 0 ? NoPlaces : new List<EntityId>(FavoritePlaces);
            if (Relationships.Count == 0) c.Relationships = NoRelationships;
            else
            {
                c.Relationships = new List<Relationship>(Relationships.Count);
                foreach (var r in Relationships) c.Relationships.Add(r.Copy());
            }
            if (Memories.Count == 0) c.Memories = NoMemories;
            else
            {
                c.Memories = new List<CharacterMemory>(Memories.Count);
                foreach (var m in Memories) c.Memories.Add(m.Copy());
            }
            c.History = History.Count == 0 ? NoHistory : new List<LifeEvent>(History);
            c.Powers = Powers?.SnapshotCopy();
            return c;
        }

        private static readonly List<string> NoStrings = new List<string>();
        private static readonly List<EntityId> NoPlaces = new List<EntityId>();
        private static readonly List<Relationship> NoRelationships = new List<Relationship>();
        private static readonly List<CharacterMemory> NoMemories = new List<CharacterMemory>();
        private static readonly List<LifeEvent> NoHistory = new List<LifeEvent>();

        public int AgeYears(long currentDay) => (int)((currentDay - BirthDay) / 365.25);

        public Relationship FindRelationship(EntityId other)
        {
            foreach (var r in Relationships) if (r.Other == other) return r;
            return null;
        }

        public Relationship FindRelationship(RelationshipType type)
        {
            foreach (var r in Relationships) if (r.Type == type) return r;
            return null;
        }

        public Relationship SetRelationship(EntityId other, RelationshipType type, float affinity, float familiarity, long day)
        {
            var existing = FindRelationship(other);
            if (existing == null)
            {
                existing = new Relationship { Other = other, SinceDay = day };
                Relationships.Add(existing);
            }
            existing.Type = type;
            existing.Affinity = affinity;
            existing.Familiarity = familiarity;
            return existing;
        }

        public void AddHistory(long day, string kind, string summary)
        {
            History.Add(new LifeEvent { Day = day, Kind = kind, Summary = summary });
            if (History.Count > MaxHistory) History.RemoveAt(0);
        }

        public CharacterMemory MemoryOf(EntityId character, bool create, long day)
        {
            foreach (var m in Memories) if (m.Character == character) return m;
            if (!create) return null;
            var memory = new CharacterMemory { Character = character, FirstMetDay = day, LastSeenDay = day };
            Memories.Add(memory);
            if (Memories.Count > MaxMemories)
            {
                // Forget the least significant, least recent acquaintance.
                var weakest = 0;
                for (var i = 1; i < Memories.Count; i++)
                    if (Significance(Memories[i]) < Significance(Memories[weakest])) weakest = i;
                Memories.RemoveAt(weakest);
            }
            return memory;
        }

        private static float Significance(CharacterMemory m)
        {
            return Math.Abs(m.Affinity) + m.Trust + m.Fear + m.Interactions * 0.05f + (m.Flags != MemoryFlags.None ? 1f : 0f) + m.LastSeenDay * 1e-5f;
        }
    }

    [Serializable]
    public sealed class Household
    {
        /// <summary>A copy sharing nothing mutable, for serializing a save off the simulation thread.</summary>
        public Household SnapshotCopy()
        {
            var c = (Household)MemberwiseClone();
            c.Members = new List<EntityId>(Members);
            return c;
        }

        public EntityId Id;
        public string Surname = "";
        public EntityId Home;
        public List<EntityId> Members = new List<EntityId>();
        public bool OwnsHome;
        public long MonthlyHousingCostCents;
    }
}
