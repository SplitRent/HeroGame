using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;

namespace HeroGame.Core.Emergency
{
    public enum EmergencyService
    {
        Police,
        Fire,
        Medical,
    }

    public enum EmergencyKind
    {
        /// <summary>A reported crime; police respond and may arrest a suspect on scene.</summary>
        Crime,
        /// <summary>A building fire; engines suppress it, it spreads and damages until they do.</summary>
        Fire,
        /// <summary>Someone hurt or ill; EMS treats and transports to hospital.</summary>
        Medical,
        /// <summary>Road collision; police and EMS.</summary>
        Collision,
    }

    public enum IncidentStatus
    {
        /// <summary>Scheduled background incident not yet called in.</summary>
        Scheduled,
        /// <summary>Called in; waiting for a free unit.</summary>
        Queued,
        Dispatched,
        OnScene,
        Resolved,
    }

    public enum UnitStatus
    {
        Available,
        EnRoute,
        OnScene,
        /// <summary>EMS carrying a patient to hospital.</summary>
        Transporting,
        Returning,
    }

    [Serializable]
    public sealed class EmergencyIncident
    {
        public EntityId Id;
        public EmergencyKind Kind;
        /// <summary>1 = life-threatening … 5 = routine.</summary>
        public int Priority = 3;
        public IncidentStatus Status;
        public WorldPosition Position;
        public EntityId District;
        public EntityId Property;
        /// <summary>Linked crime incident (Crime kind).</summary>
        public EntityId CrimeIncident;
        /// <summary>Suspect (crime) or patient (medical): a character or NPC.</summary>
        public EntityId Subject;
        public string Description = "";
        public long ReportSecond;
        public long ResolveSecond;
        public long ClosedSecond;
        public int PoliceNeeded;
        public int EnginesNeeded;
        public int AmbulancesNeeded;
        public List<EntityId> Units = new List<EntityId>();
        /// <summary>Fire intensity 0..1 (0 = out).</summary>
        public float FireIntensity;
        /// <summary>Medical severity 0..1 (drives hospital stay).</summary>
        public float Severity;
        public string Outcome = "";

        public bool Open => Status != IncidentStatus.Resolved && Status != IncidentStatus.Scheduled;
    }

    /// <summary>A crewed emergency vehicle (police car, engine, ambulance) and what it is doing.</summary>
    [Serializable]
    public sealed class EmergencyUnit
    {
        public EntityId Vehicle;
        public EmergencyService Service;
        public string CallSign = "";
        public EntityId Station;
        public WorldPosition StationPosition;
        public UnitStatus Status;
        public EntityId Incident;
        /// <summary>Road nodes of the current trip (for presentation).</summary>
        public List<int> Route = new List<int>();
        public WorldPosition From;
        public WorldPosition To;
        public long DepartSecond;
        public long ArriveSecond;
        public int CallsAnswered;

        public bool Free => Status == UnitStatus.Available || Status == UnitStatus.Returning;

        /// <summary>Where the unit is at <paramref name="second"/> (linear between trip ends; the runtime follows the road route).</summary>
        public WorldPosition PositionAt(long second)
        {
            if (Status == UnitStatus.Available) return StationPosition;
            if (Status == UnitStatus.OnScene || ArriveSecond <= DepartSecond || second >= ArriveSecond) return To;
            var t = Math.Max(0f, Math.Min(1f, (second - DepartSecond) / (float)(ArriveSecond - DepartSecond)));
            return new WorldPosition(From.X + (To.X - From.X) * t, 0f, From.Z + (To.Z - From.Z) * t);
        }
    }

    /// <summary>Response-time statistics for news and the government budget (Phase 19).</summary>
    [Serializable]
    public sealed class ResponseStats
    {
        public int Calls;
        public int Responded;
        public long TotalResponseSeconds;
        public int FiresStarted;
        public int BuildingsLost;
        public int PatientsTransported;
        public int ArrestsOnScene;

        public float AverageResponseMinutes => Responded == 0 ? 0f : TotalResponseSeconds / 60f / Responded;
    }

    [Serializable]
    public sealed class EmergencyState
    {
        public const int MaxClosedIncidents = 500;

        public List<EmergencyIncident> Incidents = new List<EmergencyIncident>();
        public List<EmergencyUnit> Units = new List<EmergencyUnit>();
        public ResponseStats Stats = new ResponseStats();
        public long SimulatedUpToSecond;
        public long LastBackgroundDay = -1;

        public EmergencyIncident Get(EntityId id)
        {
            foreach (var i in Incidents) if (i.Id == id) return i;
            return null;
        }

        public EmergencyUnit Unit(EntityId vehicle)
        {
            foreach (var u in Units) if (u.Vehicle == vehicle) return u;
            return null;
        }

        public void Trim()
        {
            var closed = 0;
            foreach (var i in Incidents) if (i.Status == IncidentStatus.Resolved) closed++;
            if (closed <= MaxClosedIncidents) return;
            var drop = closed - MaxClosedIncidents;
            Incidents.RemoveAll(i => i.Status == IncidentStatus.Resolved && drop-- > 0);
        }
    }

    /// <summary>Hospital billing (GDD §40). Pure so balance can be tuned in isolation.</summary>
    public static class MedicalBilling
    {
        public const long AmbulanceCents = 120000;
        public const long EmergencyRoomCents = 180000;
        public const long PerDayCents = 280000;

        /// <summary>Days in hospital for a severity 0..1 (a bruise is an ER visit, a critical injury a week).</summary>
        public static int StayDays(float severity) => severity < 0.25f ? 0 : (int)Math.Ceiling(severity * 7f);

        public static long Bill(float severity, bool ambulance, double priceLevel) =>
            (long)((EmergencyRoomCents + (ambulance ? AmbulanceCents : 0) + StayDays(severity) * PerDayCents) * priceLevel);
    }
}
