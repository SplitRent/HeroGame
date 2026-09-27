using System;
using System.Collections.Generic;
using HeroGame.Core.World;

namespace HeroGame.Core.Population
{
    /// <summary>Data-driven job definition (GDD §16). Loaded from occupations.json.</summary>
    [Serializable]
    public sealed class OccupationDefinition
    {
        public string Id = "";
        public string Title = "";
        public string Category = "";
        public long MinSalaryCents;
        public long MaxSalaryCents;
        public EducationLevel MinEducation;
        /// <summary>Shift start in minutes of day.</summary>
        public int ShiftStartMinute = 9 * 60;
        public int ShiftLengthMinutes = 8 * 60;
        /// <summary>Bit per weekday, bit 0 = Sunday. 62 = Mon–Fri.</summary>
        public int WorkDaysMask = 62;
        public List<PlaceKind> WorkplaceKinds = new List<PlaceKind>();
        /// <summary>Annual probability of layoff in a normal economy.</summary>
        public float LayoffRisk = 0.04f;
        /// <summary>Relative frequency in the labour market.</summary>
        public float DemandWeight = 1f;
        public string PromotesTo = "";
        /// <summary>True when the job exists in player careers (interactive career track available).</summary>
        public bool PlayerCareer;

        public bool WorksOn(DayOfWeek day) => (WorkDaysMask & (1 << (int)day)) != 0;
    }

    public sealed class OccupationTable
    {
        private readonly Dictionary<string, OccupationDefinition> _byId = new Dictionary<string, OccupationDefinition>();
        public readonly List<OccupationDefinition> All = new List<OccupationDefinition>();

        public OccupationTable(IEnumerable<OccupationDefinition> definitions)
        {
            foreach (var d in definitions)
            {
                _byId[d.Id] = d;
                All.Add(d);
            }
        }

        public OccupationDefinition Get(string id) => id != null && _byId.TryGetValue(id, out var d) ? d : null;
    }
}
