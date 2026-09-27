using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Civic
{
    public enum Department
    {
        Police,
        Fire,
        Health,
        PublicWorks,
        Parks,
        Housing,
    }

    /// <summary>Recurring civic issues (GDD §3.2). Opinion runs −1 (oppose) … +1 (support) per district.</summary>
    public static class Issues
    {
        public const string Registration = "registration";
        public const string FloodControl = "flood_control";
        public const string Taxes = "taxes";
        public const string Development = "development";
        public const string Policing = "policing";

        public static readonly string[] All = { Registration, FloodControl, Taxes, Development, Policing };
    }

    /// <summary>Civic slates (nonpartisan elections contested through slates, GDD §3.1–3.2).</summary>
    public static class Slates
    {
        public const string Renewal = "Arden Renewal";
        public const string Working = "Keep Arden Working";
        public const string Independent = "Independent";

        /// <summary>Where each slate stands on each issue (−1..1).</summary>
        public static float Stance(string slate, string issue)
        {
            switch (slate)
            {
                case Renewal:
                    return issue == Issues.Registration ? 0.8f : issue == Issues.FloodControl ? 0.7f : issue == Issues.Development ? 0.9f : issue == Issues.Policing ? 0.5f : -0.2f;
                case Working:
                    return issue == Issues.Registration ? -0.8f : issue == Issues.FloodControl ? 0.6f : issue == Issues.Development ? -0.8f : issue == Issues.Policing ? -0.6f : -0.4f;
                default:
                    return 0f;
            }
        }
    }

    /// <summary>Data-driven ordinance (ordinances.json): its issue, stance and typed effects consumed by systems.</summary>
    [Serializable]
    public sealed class OrdinanceDefinition
    {
        public string Id = "";
        public string Title = "";
        public string Description = "";
        public string Issue = "";
        /// <summary>+1 if passing it is the "support" side of the issue, −1 if opposing.</summary>
        public int Stance = 1;
        public Dictionary<string, double> Effects = new Dictionary<string, double>();
    }

    [Serializable]
    public sealed class ActiveOrdinance
    {
        public string Id = "";
        public long EnactedDay;
        public EntityId ProposedBy;
    }

    [Serializable]
    public sealed class DepartmentBudget
    {
        public Department Department;
        /// <summary>Share of the operating budget (0..1).</summary>
        public float Share;
        /// <summary>0.3..1.5 funding relative to need; drives service quality.</summary>
        public float ServiceLevel = 1f;
        public long SpentThisYearCents;
    }

    [Serializable]
    public sealed class BudgetState
    {
        public List<DepartmentBudget> Departments = new List<DepartmentBudget>();
        /// <summary>Fraction of monthly treasury income spent on operations (the rest is reserves).</summary>
        public float SpendingRate = 0.85f;
        public long LastMonthRevenueCents;
        public long LastMonthSpendingCents;
        public long LastProcessedDay = -1;

        public DepartmentBudget Of(Department d) => Departments.Find(x => x.Department == d);
    }

    public enum Office
    {
        Mayor,
        Council,
    }

    [Serializable]
    public sealed class Candidate
    {
        public EntityId Person;
        public bool IsPlayer;
        public string Name = "";
        public string Slate = Slates.Independent;
        /// <summary>For council races, the district key; empty for mayor.</summary>
        public string District = "";
        public EntityId CampaignAccount;
        public long RaisedCents;
        public long SpentCents;
        /// <summary>0..1 how well known they are (grows with spending and incumbency).</summary>
        public float Recognition = 0.1f;
        public bool Incumbent;
        public int Votes;
        /// <summary>Latest poll share (0..1).</summary>
        public float Polling;
    }

    [Serializable]
    public sealed class Election
    {
        public string Id = "";
        public Office Office;
        public string District = "";
        public long FilingOpensDay;
        public long ElectionDay;
        public List<Candidate> Candidates = new List<Candidate>();
        public List<EntityId> PlayerVoters = new List<EntityId>();
        public List<string> PlayerBallots = new List<string>();
        public bool Held;
        public EntityId Winner;
        public int Turnout;
        public int EligibleVoters;
    }

    [Serializable]
    public sealed class Officeholder
    {
        public Office Office;
        public string District = "";
        public EntityId Person;
        public string Name = "";
        public string Slate = "";
        public bool IsPlayer;
        public long TermEndsDay;
    }

    [Serializable]
    public sealed class Proposal
    {
        public string OrdinanceId = "";
        public EntityId ProposedBy;
        public long VoteDay;
        public bool Decided;
        public bool Passed;
        public int Ayes;
        public int Nays;
        public string Repeal = "";
        /// <summary>Council votes cast by player officeholders (character id → aye).</summary>
        public Dictionary<string, bool> PlayerVotes = new Dictionary<string, bool>();
    }

    [Serializable]
    public sealed class DistrictOpinion
    {
        public EntityId District;
        public Dictionary<string, float> Support = new Dictionary<string, float>();

        public float Of(string issue) => Support.TryGetValue(issue, out var v) ? v : 0f;
    }

    [Serializable]
    public sealed class CivicState
    {
        public BudgetState Budget = new BudgetState();
        public List<ActiveOrdinance> Ordinances = new List<ActiveOrdinance>();
        public List<Proposal> Proposals = new List<Proposal>();
        public List<DistrictOpinion> Opinion = new List<DistrictOpinion>();
        public List<Officeholder> Officeholders = new List<Officeholder>();
        public List<Election> Elections = new List<Election>();
        public List<Election> PastElections = new List<Election>();
        /// <summary>Characters registered under the anomalous-abilities ordinance.</summary>
        public List<EntityId> RegisteredPowered = new List<EntityId>();
        /// <summary>
        /// Each player candidate's campaign account (character id → account id). Money left after an election stays
        /// there for their next run: it is campaign money, not personal money.
        /// </summary>
        public Dictionary<string, EntityId> CampaignAccounts = new Dictionary<string, EntityId>();
        public long LastElectionScheduledDay = -1;

        public bool IsActive(string ordinance) => Ordinances.Exists(o => o.Id == ordinance);

        public DistrictOpinion OpinionOf(EntityId district)
        {
            var o = Opinion.Find(x => x.District == district);
            if (o == null)
            {
                o = new DistrictOpinion { District = district };
                Opinion.Add(o);
            }
            return o;
        }
    }

    /// <summary>A dated event on the city calendar (calendar_events.json).</summary>
    [Serializable]
    public sealed class CalendarEvent
    {
        public string Id = "";
        public string Name = "";
        public int Month;
        public int Day;
        public int Days = 1;
        /// <summary>Demand multiplier by place kind name (Nightlife, Restaurant, Shop…).</summary>
        public Dictionary<string, float> Demand = new Dictionary<string, float>();
        public string Announcement = "";
    }

    public enum DisasterKind
    {
        FlashFlood,
        ChemicalIncident,
        HeatWave,
        Blackout,
    }

    [Serializable]
    public sealed class ActiveDisaster
    {
        public DisasterKind Kind;
        public EntityId District;
        public long StartSecond;
        public long EndSecond;
        public float Severity;
        public string Headline = "";
    }

    [Serializable]
    public sealed class DisasterState
    {
        public List<ActiveDisaster> Active = new List<ActiveDisaster>();
        public List<ActiveDisaster> Recent = new List<ActiveDisaster>();
        public int Total;
        /// <summary>Blackout hours per district key today (feeds the business day).</summary>
        public Dictionary<string, float> OutageHoursToday = new Dictionary<string, float>();
        /// <summary>Hours each district key spent under an emergency closure today.</summary>
        public Dictionary<string, int> ClosureHoursToday = new Dictionary<string, int>();

        public bool ClosesDistrict(EntityId district, long now) =>
            Active.Exists(d => d.District == district && d.Kind == DisasterKind.ChemicalIncident && now >= d.StartSecond && now < d.EndSecond);
    }
}
