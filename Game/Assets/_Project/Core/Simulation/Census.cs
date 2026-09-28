using System.Collections.Generic;

namespace HeroGame.Core.Simulation
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Population;

    /// <summary>
    /// Who lives where, counted once per game day and shared by every system that needs it (emergency call rates,
    /// electorates, the budget). Before this each system rescanned the whole population, several times a day.
    /// Lists are in stable id order. Deaths during the day are filtered by the users (they check Alive).
    /// </summary>
    public sealed class Census
    {
        public long Day = long.MinValue;
        public int PopulationCount = -1;
        public int Alive;
        public readonly Dictionary<EntityId, int> ResidentsByDistrict = new Dictionary<EntityId, int>();
        public readonly Dictionary<EntityId, List<NpcRecord>> AdultsByDistrict = new Dictionary<EntityId, List<NpcRecord>>();
        public readonly List<NpcRecord> Adults = new List<NpcRecord>();

        public int ResidentsOf(EntityId district) => ResidentsByDistrict.TryGetValue(district, out var n) ? n : 0;

        public IReadOnlyList<NpcRecord> AdultsOf(EntityId district) =>
            AdultsByDistrict.TryGetValue(district, out var list) ? list : (IReadOnlyList<NpcRecord>)System.Array.Empty<NpcRecord>();

        internal void Take(World w, long day)
        {
            Day = day;
            PopulationCount = w.Population.Count;
            Alive = 0;
            ResidentsByDistrict.Clear();
            AdultsByDistrict.Clear();
            Adults.Clear();
            foreach (var npc in w.Population.Ordered)
            {
                if (!npc.Alive) continue;
                Alive++;
                var home = w.Geography.GetPlace(npc.Home);
                var district = home != null ? home.District : EntityId.None;
                if (home != null) ResidentsByDistrict[district] = ResidentsByDistrict.TryGetValue(district, out var n) ? n + 1 : 1;
                if (npc.AgeYears(day) < 18) continue;
                Adults.Add(npc);
                if (home == null) continue;
                if (!AdultsByDistrict.TryGetValue(district, out var list)) AdultsByDistrict[district] = list = new List<NpcRecord>();
                list.Add(npc);
            }
        }
    }
}
