using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;
using HeroGame.Core.Time;

namespace HeroGame.Core.Crime
{
    /// <summary>Item catalog entry (items.json): loot, stolen goods, tools and contraband.</summary>
    [Serializable]
    public sealed class ItemDefinition
    {
        public string Id = "";
        public string DisplayName = "";
        public string Category = "";
        /// <summary>Retail value; fences pay a fraction of it.</summary>
        public long ValueCents;
        /// <summary>False for contraband (possession is itself an offence).</summary>
        public bool Legal = true;
        /// <summary>Where this item turns up as loot (residence_low, residence_high, retail_general, vehicle…).</summary>
        public List<string> LootTags = new List<string>();
    }

    public enum CaseStage
    {
        /// <summary>Charged; the defendant is in custody awaiting a bail decision/hearing.</summary>
        Charged,
        /// <summary>Released on bail until the hearing.</summary>
        OnBail,
        /// <summary>Held without bail (or could not pay) until the hearing.</summary>
        HeldInCustody,
        Closed,
    }

    public enum Verdict
    {
        Pending,
        Guilty,
        GuiltyPlea,
        Acquitted,
        Dismissed,
    }

    public enum Counsel
    {
        PublicDefender,
        PrivateAttorney,
    }

    [Serializable]
    public sealed class Sentence
    {
        public long FineCents;
        public int JailDays;
        public int ProbationDays;
    }

    /// <summary>One prosecution (GDD §32): the charges from an arrest, bail, hearing and outcome.</summary>
    [Serializable]
    public sealed class CourtCase
    {
        public EntityId Id;
        public EntityId Defendant;
        public List<Charge> Charges = new List<Charge>();
        public CaseStage Stage;
        public long ArrestDay;
        public long HearingDay;
        public long BailCents;
        public bool BailPaid;
        public Counsel Counsel;
        public bool PleaOffered;
        public bool PleadedGuilty;
        /// <summary>Strength of the prosecution's evidence 0..1 (noisy-OR over incidents).</summary>
        public float EvidenceStrength;
        public Verdict Verdict;
        public Sentence Sentence;
        public string Summary = "";
    }

    /// <summary>
    /// Persistent justice state: incidents, the evidence on file and every court case. Evidence stays on file
    /// after a pursuit ends, so cold cases can be charged when a suspect is finally arrested.
    /// </summary>
    [Serializable]
    public sealed class JusticeState
    {
        public const int MaxClosedIncidents = 2000;

        public List<CrimeIncident> Incidents = new List<CrimeIncident>();
        public List<EvidenceItem> Evidence = new List<EvidenceItem>();
        public List<CourtCase> Cases = new List<CourtCase>();

        public CrimeIncident Incident(EntityId id)
        {
            foreach (var i in Incidents) if (i.Id == id) return i;
            return null;
        }

        public CourtCase OpenCaseFor(EntityId defendant)
        {
            foreach (var c in Cases) if (c.Defendant == defendant && c.Stage != CaseStage.Closed) return c;
            return null;
        }

        /// <summary>How strongly the evidence ties <paramref name="suspect"/> to one incident (civilian identity).</summary>
        public float StrengthFor(EntityId incident, EntityId suspect, bool aliasLinked)
        {
            var miss = 1.0;
            foreach (var e in Evidence)
            {
                if (e.Incident != incident || e.Suspect != suspect) continue;
                if (e.PointsToAlias && !aliasLinked) continue;
                miss *= 1.0 - Math.Max(0f, Math.Min(0.99f, e.Confidence));
            }
            return (float)(1.0 - miss);
        }

        /// <summary>Drops the oldest solved incidents (and their evidence) beyond the retention cap.</summary>
        public void Trim()
        {
            var solved = 0;
            foreach (var i in Incidents) if (i.Solved) solved++;
            if (solved <= MaxClosedIncidents) return;
            var remove = new HashSet<EntityId>();
            foreach (var i in Incidents)
            {
                if (solved <= MaxClosedIncidents) break;
                if (!i.Solved) continue;
                remove.Add(i.Id);
                solved--;
            }
            Incidents.RemoveAll(i => remove.Contains(i.Id));
            Evidence.RemoveAll(e => remove.Contains(e.Incident));
        }
    }

    /// <summary>Sentencing and bail rules. Pure so they can be tuned and tested in isolation.</summary>
    public static class SentencingGuidelines
    {
        /// <summary>Evidence strength a prosecutor needs to file a charge.</summary>
        public const float ChargeThreshold = 0.5f;
        public const int HearingDelayDays = 3;
        public const long PrivateAttorneyFeeCents = 450000;

        public static int PriorConvictions(CriminalRecord record) => record != null ? record.Convictions : 0;

        public static long Bail(IReadOnlyList<CrimeType> charges, CriminalRecord record, bool onProbation)
        {
            long bail = 0;
            var maxSeverity = 0;
            foreach (var c in charges)
            {
                bail += c.BaseFineCents + c.Severity * c.Severity * 25000L;
                maxSeverity = Math.Max(maxSeverity, c.Severity);
            }
            if (maxSeverity >= 10) return -1; // held without bail
            var multiplier = 1.0 + 0.5 * PriorConvictions(record) + (onProbation ? 1.0 : 0.0);
            return (long)Math.Round(bail * multiplier / 10000.0) * 10000;
        }

        /// <summary>Chance a trial ends in conviction, from evidence, counsel and a guilty plea.</summary>
        public static double ConvictionChance(float evidence, Counsel counsel)
        {
            var p = 0.08 + 0.9 * Math.Pow(Math.Max(0f, Math.Min(1f, evidence)), 1.3);
            if (counsel == Counsel.PrivateAttorney) p *= 0.78;
            return Math.Max(0.02, Math.Min(0.97, p));
        }

        /// <summary>
        /// Sentence for convicted charges. Repeat offending and probation violations aggravate; a guilty plea
        /// mitigates by a third. <paramref name="scale"/> converts guideline days into served game days.
        /// </summary>
        public static Sentence For(IReadOnlyList<CrimeType> charges, CriminalRecord record, bool plea, bool onProbation, float scale)
        {
            long fine = 0;
            double jail = 0;
            var maxSeverity = 0;
            foreach (var c in charges)
            {
                fine += c.BaseFineCents;
                jail += c.BaseJailDays;
                maxSeverity = Math.Max(maxSeverity, c.Severity);
            }
            var aggravation = 1.0 + 0.35 * Math.Min(6, PriorConvictions(record)) + (onProbation ? 0.5 : 0.0);
            var mitigation = plea ? 0.67 : 1.0;
            var s = new Sentence
            {
                FineCents = (long)Math.Round(fine * aggravation * mitigation / 100.0) * 100,
                JailDays = jail <= 0 ? 0 : Math.Max(1, (int)Math.Round(jail * aggravation * mitigation * Math.Max(0.01f, scale))),
            };
            // First offenders for minor crimes get probation instead of jail.
            if (PriorConvictions(record) == 0 && maxSeverity <= 4 && !onProbation)
            {
                s.ProbationDays = Math.Max(30, s.JailDays * 6);
                s.JailDays = 0;
            }
            else if (s.JailDays > 0)
            {
                s.ProbationDays = s.JailDays * 2;
            }
            return s;
        }
    }
}
