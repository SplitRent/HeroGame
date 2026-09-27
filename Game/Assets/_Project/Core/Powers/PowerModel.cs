using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Powers
{
    /// <summary>What part of reality a power touches (GDD §40). Persisted; append only.</summary>
    public enum PowerDomain
    {
        Force,
        Movement,
        Energy,
        Matter,
        Gravity,
        Time,
        Space,
        Biology,
        Perception,
        Transformation,
        Creation,
        Manipulation,
        Defense,
        Environment,
    }

    /// <summary>What the power does within its domain.</summary>
    public enum EffectVerb
    {
        Enhance,
        Project,
        Manipulate,
        Generate,
        Absorb,
        Shield,
        Sense,
        Transform,
        Traverse,
        Duplicate,
        Copy,
        Warp,
    }

    /// <summary>How the effect reaches the world.</summary>
    public enum DeliveryMode
    {
        Self,
        Touch,
        Projectile,
        Beam,
        Area,
        Field,
        Remote,
    }

    /// <summary>Physical character of an effect; drives the interaction framework (GDD §109).</summary>
    public enum PowerElement
    {
        None,
        Kinetic,
        Electric,
        Fire,
        Cold,
        Light,
        Sound,
        Gravitic,
        Temporal,
        Spatial,
        Biological,
        Psychic,
    }

    public enum LimitationKind
    {
        Stamina,
        Cooldown,
        Concentration,
        EnvironmentRequirement,
        PhysicalStrain,
        Instability,
        LineOfSight,
        MassLimit,
        Duration,
    }

    /// <summary>Where the character is in coming to terms with an ability (GDD §43).</summary>
    public enum PowerStage
    {
        /// <summary>Exposed; nothing noticeable yet.</summary>
        Latent,
        /// <summary>Involuntary symptoms: flickering lights, shaking objects. Player does not know what is happening.</summary>
        Manifesting,
        /// <summary>First deliberate use. The player can now attempt it, unreliably.</summary>
        Aware,
        Practiced,
        Mastered,
    }

    [Serializable]
    public sealed class PowerComponent
    {
        public PowerDomain Domain;
        public EffectVerb Verb;
        public DeliveryMode Delivery;
        public PowerElement Element;
        /// <summary>0..1 raw output (force, speed, energy).</summary>
        public float Magnitude = 0.5f;
        /// <summary>Metres (0 for Self).</summary>
        public float Range;
        /// <summary>0..1 control fidelity.</summary>
        public float Precision = 0.5f;
        /// <summary>0..1: how much stamina/strain each use costs (1 = very cheap).</summary>
        public float Efficiency = 0.5f;
    }

    [Serializable]
    public sealed class PowerLimitation
    {
        public LimitationKind Kind;
        /// <summary>0..1 how restrictive.</summary>
        public float Severity = 0.5f;
        /// <summary>For EnvironmentRequirement: e.g. "water", "darkness", "metal", "heat".</summary>
        public string Requirement = "";
    }

    /// <summary>A branch the ability can evolve into with practice (GDD §44).</summary>
    [Serializable]
    public sealed class PowerEvolution
    {
        public string Id = "";
        public string Description = "";
        /// <summary>Mastery experience required.</summary>
        public float RequiredExperience = 100f;
        public string Requires = "";
    }

    /// <summary>
    /// Authoring template the generator samples from. Archetypes are data (power_archetypes.json),
    /// so new powers are added without code — the runtime executes components, not archetype ids.
    /// </summary>
    [Serializable]
    public sealed class PowerArchetype
    {
        public string Id = "";
        /// <summary>Internal descriptive label. Players never see this until they name the power themselves.</summary>
        public string InternalName = "";
        public List<PowerComponent> Components = new List<PowerComponent>();
        public List<PowerElement> AllowedElements = new List<PowerElement>();
        public List<LimitationKind> TypicalLimitations = new List<LimitationKind>();
        public List<PowerEvolution> Evolutions = new List<PowerEvolution>();
        /// <summary>Symptoms shown while Manifesting (keys into the runtime's symptom library).</summary>
        public List<string> Symptoms = new List<string>();
    }

    /// <summary>A concrete, persisted ability instance. Two people with the "same" archetype still differ.</summary>
    [Serializable]
    public sealed class PowerDefinition
    {
        public string ArchetypeId = "";
        /// <summary>True for procedurally composed powers with no archetype (the rarest and strangest).</summary>
        public bool Novel;
        public ulong Seed;
        public List<PowerComponent> Components = new List<PowerComponent>();
        public List<PowerLimitation> Limitations = new List<PowerLimitation>();
        public float Complexity;

        public PowerElement PrimaryElement => Components.Count > 0 ? Components[0].Element : PowerElement.None;

        public bool HasDomain(PowerDomain domain)
        {
            foreach (var c in Components) if (c.Domain == domain) return true;
            return false;
        }

        /// <summary>Descriptive label derived from components, e.g. "Kinetic Force Enhancement".</summary>
        public string Describe()
        {
            if (Components.Count == 0) return "Unknown";
            var c = Components[0];
            var element = c.Element != PowerElement.None ? c.Element + " " : "";
            return element + c.Domain + " " + c.Verb + (Components.Count > 1 ? " (+" + (Components.Count - 1) + ")" : "");
        }
    }

    [Serializable]
    public sealed class PowerProgress
    {
        public float Experience;
        public float StrengthBonus;
        public float PrecisionBonus;
        public float RangeBonus;
        public float EfficiencyBonus;
        public int SuccessfulUses;
        public int FailedUses;
        public List<string> UnlockedEvolutions = new List<string>();
    }

    [Serializable]
    public sealed class PowerInstance
    {
        public PowerDefinition Definition = new PowerDefinition();
        public PowerStage Stage;
        public long AcquiredDay;
        public long StageChangedDay;
        /// <summary>Incubation: day on which Latent becomes Manifesting.</summary>
        public long ManifestDay;
        public PowerProgress Progress = new PowerProgress();
        /// <summary>Name the player gave it (empty until they do).</summary>
        public string PlayerLabel = "";
        public EntityId SourceEvent;
    }

    /// <summary>Everything supernatural about one character on one server.</summary>
    [Serializable]
    public sealed class CharacterPowers
    {
        public List<PowerInstance> Powers = new List<PowerInstance>();
        public int Exposures;
        /// <summary>0..1 current physical strain; high strain causes failures and injury.</summary>
        public float Strain;
        /// <summary>0..1 stamina pool shared across abilities.</summary>
        public float Stamina = 1f;

        public bool IsPowered
        {
            get
            {
                foreach (var p in Powers) if (p.Stage >= PowerStage.Manifesting) return true;
                return false;
            }
        }
    }
}
