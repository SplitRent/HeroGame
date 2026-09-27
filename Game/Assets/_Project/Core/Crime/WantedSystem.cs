using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;

namespace HeroGame.Core.Crime
{
    public enum WantedPhase
    {
        /// <summary>Police have no active interest.</summary>
        Clear,
        /// <summary>A crime was reported but the suspect is not yet identified or located.</summary>
        Investigating,
        /// <summary>Police know who/what they are looking for and are searching the last known area.</summary>
        Searching,
        /// <summary>Police have eyes on the suspect.</summary>
        Pursuit,
    }

    [Serializable]
    public sealed class WantedStatus
    {
        public EntityId Suspect;
        public WantedPhase Phase;
        /// <summary>0..5 response level. Derived, never set arbitrarily.</summary>
        public int Level;
        /// <summary>Highest severity among crimes police currently attribute to the suspect.</summary>
        public int Severity;
        /// <summary>Police confidence that they know who the suspect is (noisy-OR of evidence).</summary>
        public float Identification;
        public WorldPosition LastKnownPosition;
        public GameDateTime LastSeen;
        public float SearchRadius;
        public int OffencesThisEpisode;
        public bool ViolenceAgainstPolice;
        public bool PoweredSuspect;
    }

    public sealed class WantedSettings
    {
        /// <summary>Identification needed to put a named suspect on the radio.</summary>
        public float IdentifyThreshold = 0.55f;
        public float SearchRadiusStart = 80f;
        public float SearchRadiusGrowthPerMinute = 40f;
        public float SearchRadiusMax = 900f;
        /// <summary>Game minutes of searching before police stand down (scaled by level).</summary>
        public float SearchMinutesPerLevel = 6f;
        public float ResponseMultiplier = 1f;
    }

    /// <summary>
    /// Logic-driven wanted system (GDD §29). Police response escalates only from what police
    /// actually know: reported incidents, collected evidence, and direct observation. Wanted level
    /// is a function of severity, repetition, identification and visibility — not a counter.
    /// Losing police means breaking line of sight and leaving the growing search area; if the suspect
    /// was identified, a warrant remains on their record after the episode ends.
    /// </summary>
    public sealed class WantedSystem
    {
        private readonly Dictionary<EntityId, WantedStatus> _status = new Dictionary<EntityId, WantedStatus>();
        private readonly Dictionary<EntityId, List<EvidenceItem>> _evidence = new Dictionary<EntityId, List<EvidenceItem>>();
        public readonly WantedSettings Settings;

        public event Action<WantedStatus> LevelChanged;
        /// <summary>Raised when an episode ends with the suspect identified (warrant issued).</summary>
        public event Action<EntityId> WarrantIssued;

        public WantedSystem(WantedSettings settings = null)
        {
            Settings = settings ?? new WantedSettings();
        }

        public WantedStatus Get(EntityId suspect) => _status.TryGetValue(suspect, out var s) ? s : null;
        public IEnumerable<WantedStatus> Active => _status.Values;

        public IReadOnlyList<EvidenceItem> EvidenceAgainst(EntityId suspect)
        {
            return _evidence.TryGetValue(suspect, out var list) ? (IReadOnlyList<EvidenceItem>)list : Array.Empty<EvidenceItem>();
        }

        /// <summary>Combined identification confidence for the civilian identity (alias evidence excluded).</summary>
        public float IdentificationOf(EntityId suspect)
        {
            if (!_evidence.TryGetValue(suspect, out var list)) return 0f;
            var miss = 1.0;
            foreach (var e in list) if (!e.PointsToAlias) miss *= 1.0 - Math.Max(0f, Math.Min(0.99f, e.Confidence));
            return (float)(1.0 - miss);
        }

        public void AddEvidence(EvidenceItem item)
        {
            if (!_evidence.TryGetValue(item.Suspect, out var list))
            {
                list = new List<EvidenceItem>();
                _evidence.Add(item.Suspect, list);
            }
            list.Add(item);
            if (_status.TryGetValue(item.Suspect, out var s))
            {
                s.Identification = IdentificationOf(item.Suspect);
                Recompute(s);
            }
        }

        /// <summary>Police become aware of a crime (via witness call, alarm, camera operator or officer).</summary>
        public WantedStatus ReportCrime(CrimeIncident incident, CrimeType type, GameDateTime now, bool policeWitnessed, bool poweredSuspect = false)
        {
            incident.ReportedToPolice = true;
            incident.ReportedAt = now;
            var s = GetOrCreate(incident.Perpetrator);
            s.Severity = Math.Max(s.Severity, type.Severity);
            s.OffencesThisEpisode++;
            s.PoweredSuspect |= poweredSuspect;
            s.LastKnownPosition = incident.Position;
            s.LastSeen = now;
            s.SearchRadius = Settings.SearchRadiusStart;
            s.Identification = IdentificationOf(incident.Perpetrator);
            if (policeWitnessed)
            {
                s.Phase = WantedPhase.Pursuit;
                s.Identification = Math.Max(s.Identification, 0.6f);
            }
            else if (s.Phase == WantedPhase.Clear)
            {
                s.Phase = WantedPhase.Investigating;
            }
            Recompute(s);
            return s;
        }

        /// <summary>Called by police perception each time officers/cameras/helicopter can see the suspect.</summary>
        public void ObserveSuspect(EntityId suspect, WorldPosition position, GameDateTime now)
        {
            if (!_status.TryGetValue(suspect, out var s) || s.Phase == WantedPhase.Clear) return;
            // Investigating → only escalates to pursuit when police know who they're looking for.
            if (s.Phase == WantedPhase.Investigating && s.Identification < Settings.IdentifyThreshold) return;
            s.Phase = WantedPhase.Pursuit;
            s.LastKnownPosition = position;
            s.LastSeen = now;
            s.SearchRadius = Settings.SearchRadiusStart;
            Recompute(s);
        }

        public void AssaultedOfficer(EntityId suspect)
        {
            var s = GetOrCreate(suspect);
            s.ViolenceAgainstPolice = true;
            if (s.Phase == WantedPhase.Clear) s.Phase = WantedPhase.Pursuit;
            Recompute(s);
        }

        /// <summary>Advances search/decay logic. Call regularly with current game time.</summary>
        public void Tick(GameDateTime now, Func<EntityId, WorldPosition?> currentPositionOf)
        {
            var ended = new List<EntityId>();
            foreach (var s in _status.Values)
            {
                if (s.Phase == WantedPhase.Clear) continue;
                var minutesSinceSeen = (now - s.LastSeen) / 60f;
                if (s.Phase == WantedPhase.Pursuit && minutesSinceSeen > 0.5f)
                {
                    s.Phase = WantedPhase.Searching;
                }
                if (s.Phase == WantedPhase.Searching)
                {
                    s.SearchRadius = Math.Min(Settings.SearchRadiusMax, Settings.SearchRadiusStart + minutesSinceSeen * Settings.SearchRadiusGrowthPerMinute);
                    var pos = currentPositionOf?.Invoke(s.Suspect);
                    var outside = pos.HasValue && WorldPosition.DistanceXZ(pos.Value, s.LastKnownPosition) > s.SearchRadius;
                    var searchBudget = Settings.SearchMinutesPerLevel * Math.Max(1, s.Level) * Settings.ResponseMultiplier;
                    if (outside || minutesSinceSeen > searchBudget * 2.5f) ended.Add(s.Suspect);
                }
                else if (s.Phase == WantedPhase.Investigating && minutesSinceSeen > 60f * 24f)
                {
                    ended.Add(s.Suspect); // investigation goes cold for now; evidence remains on file.
                }
                Recompute(s);
            }
            foreach (var id in ended) EndEpisode(id);
        }

        public void Arrested(EntityId suspect)
        {
            _status.Remove(suspect);
        }

        private void EndEpisode(EntityId suspect)
        {
            var s = _status[suspect];
            if (s.Identification >= Settings.IdentifyThreshold) WarrantIssued?.Invoke(suspect);
            _status.Remove(suspect);
            s.Phase = WantedPhase.Clear;
            s.Level = 0;
            LevelChanged?.Invoke(s);
        }

        private WantedStatus GetOrCreate(EntityId suspect)
        {
            if (!_status.TryGetValue(suspect, out var s))
            {
                s = new WantedStatus { Suspect = suspect, Phase = WantedPhase.Clear };
                _status.Add(suspect, s);
            }
            return s;
        }

        /// <summary>
        /// Level table (TDD §10.3):
        /// 1 patrol responds to report · 2 multiple units converge on last known position ·
        /// 3 active pursuit, roadblocks · 4 air unit, tactical team · 5 city-wide mobilisation.
        /// </summary>
        private void Recompute(WantedStatus s)
        {
            var before = s.Level;
            if (s.Phase == WantedPhase.Clear)
            {
                s.Level = 0;
            }
            else
            {
                var score = s.Severity * 0.45f + Math.Max(0, s.OffencesThisEpisode - 1) * 0.6f;
                if (s.Phase == WantedPhase.Pursuit) score += 1f;
                if (s.ViolenceAgainstPolice) score += 2f;
                if (s.PoweredSuspect) score += 1.5f;
                if (s.Phase == WantedPhase.Investigating && s.Identification < Settings.IdentifyThreshold) score = Math.Min(score, 1.5f);
                s.Level = Math.Max(1, Math.Min(5, (int)Math.Ceiling(score * Settings.ResponseMultiplier / 1.5f)));
            }
            if (s.Level != before) LevelChanged?.Invoke(s);
        }
    }
}
