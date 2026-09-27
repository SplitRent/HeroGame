using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;

namespace HeroGame.Core.Crime
{
    public enum CrimeCategory
    {
        Petty,
        Property,
        Violent,
        Organized,
        Major,
        Traffic,
        Anomalous,
    }

    /// <summary>Data-driven crime definition (loaded from crime_types.json).</summary>
    [Serializable]
    public sealed class CrimeType
    {
        public string Id = "";
        public string DisplayName = "";
        public CrimeCategory Category;
        /// <summary>1..10. Drives police escalation, penalties and news coverage.</summary>
        public int Severity = 1;
        public long BaseFineCents;
        public int BaseJailDays;
        /// <summary>How likely a bystander calls it in (0..1) before district trust modifiers.</summary>
        public float ReportLikelihood = 0.5f;
    }

    public enum EvidenceKind
    {
        Eyewitness,
        Cctv,
        VehiclePlate,
        VehicleDescription,
        Fingerprint,
        Dna,
        Digital,
        Photograph,
        PlayerRecording,
        PoliceObservation,
        EnergySignature,
        PhysicalDamage,
    }

    /// <summary>
    /// One piece of evidence linking a suspect (or a suspect's alias) to an incident.
    /// Confidence is how strongly it identifies the civilian identity; disguises and masks
    /// reduce it at creation time.
    /// </summary>
    [Serializable]
    public sealed class EvidenceItem
    {
        public EvidenceKind Kind;
        public EntityId Incident;
        public EntityId Suspect;
        /// <summary>0..1 strength of identification.</summary>
        public float Confidence;
        public GameDateTime CollectedAt;
        /// <summary>Source NPC (witness) or place (camera).</summary>
        public EntityId Source;
        /// <summary>True when the evidence points at a masked alias rather than the civilian.</summary>
        public bool PointsToAlias;
        public string Note = "";
    }

    [Serializable]
    public sealed class CrimeIncident
    {
        public EntityId Id;
        public string CrimeTypeId = "";
        public EntityId Perpetrator;
        public EntityId Victim;
        public EntityId District;
        public WorldPosition Position;
        public GameDateTime OccurredAt;
        public bool ReportedToPolice;
        public GameDateTime ReportedAt;
        public List<EntityId> Witnesses = new List<EntityId>();
        public bool Solved;
        public long ValueStolenCents;
    }

    [Serializable]
    public sealed class Charge
    {
        public string CrimeTypeId = "";
        public EntityId Incident;
        public GameDateTime FiledAt;
        public bool Convicted;
        public bool Dismissed;
    }

    /// <summary>Police record for a character or NPC (GDD §32).</summary>
    [Serializable]
    public sealed class CriminalRecord
    {
        public EntityId Subject;
        public List<Charge> Charges = new List<Charge>();
        public bool ActiveWarrant;
        public int Arrests;
        public int Convictions;
        public long FinesOwedCents;
        public GameDateTime ProbationUntil;
        /// <summary>Day unpaid fines fall due; missing it turns into a warrant.</summary>
        public long FinesDueDay = -1;
        /// <summary>Held by police/court (arrested, awaiting hearing, or serving a sentence).</summary>
        public bool InCustody;
        /// <summary>Release day while serving a jail sentence (-1 while held pending a hearing).</summary>
        public long CustodyUntilDay = -1;
        public bool ServingSentence;
        public bool WarrantForFines;

        public bool OnProbation(GameDateTime now) => ProbationUntil > now;
    }

    /// <summary>A detected interaction between identities: disguise quality at the time of a crime.</summary>
    public struct ConcealmentState
    {
        /// <summary>0 = face fully visible, 1 = fully concealed.</summary>
        public float FaceConcealment;
        /// <summary>True when dressed as a known alias (costume) — evidence then attaches to the alias.</summary>
        public bool InAliasCostume;
        /// <summary>Vehicle plates obscured/removed.</summary>
        public bool PlatesHidden;
    }
}
