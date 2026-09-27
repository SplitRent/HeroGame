using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Crime
{
    /// <summary>Something that saw a crime: an NPC, a player, or a camera.</summary>
    public struct Observer
    {
        public EntityId Id;
        public bool IsCamera;
        public float Distance;
        /// <summary>0..1 light/visibility at the scene (night, fog, rain reduce it).</summary>
        public float Visibility;
        /// <summary>NPC personality: willingness to get involved (agreeableness etc.) 0..1.</summary>
        public float Civic;
        /// <summary>True if the observer already knows the perpetrator (friend, coworker…).</summary>
        public bool KnowsPerpetrator;
        /// <summary>Camera is actively monitored (bank, police) → reports immediately.</summary>
        public bool Monitored;
    }

    public struct WitnessOutcome
    {
        public bool Reported;
        public float ReportDelayMinutes;
        public List<EvidenceItem> Evidence;
    }

    /// <summary>
    /// Turns who-saw-what into evidence and a police report (GDD §29, §31). Witnesses are fallible:
    /// distance, visibility and concealment reduce identification; district trust in police and
    /// personal civic-mindedness determine whether anyone calls it in.
    /// </summary>
    public static class WitnessModel
    {
        public static WitnessOutcome Evaluate(CrimeIncident incident, CrimeType type, IReadOnlyList<Observer> observers,
            ConcealmentState concealment, float districtPoliceTrust, DeterministicRandom rng)
        {
            var outcome = new WitnessOutcome { Evidence = new List<EvidenceItem>(), ReportDelayMinutes = float.MaxValue };
            foreach (var o in observers)
            {
                var clarity = Math.Max(0f, 1f - o.Distance / (o.IsCamera ? 45f : 30f)) * o.Visibility;
                if (clarity <= 0.02f) continue;

                var faceVisible = 1f - concealment.FaceConcealment;
                var identification = clarity * (o.IsCamera ? 0.75f : 0.5f) * faceVisible;
                if (o.KnowsPerpetrator && faceVisible > 0.4f) identification = Math.Max(identification, 0.85f * clarity + 0.1f);
                identification = Math.Min(0.95f, identification);

                if (identification > 0.05f || concealment.InAliasCostume)
                {
                    outcome.Evidence.Add(new EvidenceItem
                    {
                        Kind = o.IsCamera ? EvidenceKind.Cctv : EvidenceKind.Eyewitness,
                        Incident = incident.Id,
                        Suspect = incident.Perpetrator,
                        Confidence = identification,
                        CollectedAt = incident.OccurredAt,
                        Source = o.Id,
                        PointsToAlias = concealment.InAliasCostume,
                        Note = o.IsCamera ? "Camera footage" : "Witness statement",
                    });
                }
                if (!o.IsCamera) incident.Witnesses.Add(o.Id);

                // Reporting decision.
                float reportChance;
                float delay;
                if (o.IsCamera)
                {
                    reportChance = o.Monitored ? 0.95f : 0f; // unmonitored footage is only reviewed during investigation
                    delay = 1f;
                }
                else
                {
                    reportChance = type.ReportLikelihood * (0.4f + districtPoliceTrust * 0.8f) * (0.5f + o.Civic * 0.7f) * Math.Min(1f, clarity * 1.5f + 0.2f);
                    if (o.KnowsPerpetrator) reportChance *= 0.6f;
                    delay = 1.5f + (float)rng.NextDouble() * 6f;
                }
                if (rng.Chance(Math.Min(0.98f, reportChance)))
                {
                    outcome.Reported = true;
                    outcome.ReportDelayMinutes = Math.Min(outcome.ReportDelayMinutes, delay);
                }
            }
            if (!outcome.Reported) outcome.ReportDelayMinutes = 0f;
            return outcome;
        }
    }
}
