using System;
using System.Collections.Generic;

namespace HeroGame.Core.Population
{
    using HeroGame.Core.Foundation;
    using HeroGame.Core.World;

    /// <summary>
    /// Creates the initial population for a world from its residences and workplaces (GDD §12).
    /// Fully deterministic for a given seed and content set, so every server created with the
    /// same seed starts with the same people — and Story Mode has a canonical population.
    /// </summary>
    public sealed class PopulationGenerator
    {
        private readonly ulong _seed;
        public const int VacantHomePercent = 7;
        private readonly IdAllocator _ids;
        private readonly OccupationTable _occupations;
        private readonly NameTables _names;
        private readonly Geography _geography;

        public PopulationGenerator(ulong seed, IdAllocator ids, OccupationTable occupations, NameTables names, Geography geography)
        {
            _seed = seed;
            _ids = ids;
            _occupations = occupations;
            _names = names != null && names.IsUsable ? names : NameTables.Fallback();
            _geography = geography;
        }

        /// <param name="density">Server NPC density multiplier (0.1–2).</param>
        public void Generate(PopulationRegistry registry, long startDay, float density)
        {
            var rng = new DeterministicRandom(StableHash.Combine(_seed, 0x9E0917));
            var workplaces = LifeSimContext.IndexWorkplaces(_geography);
            var schools = _geography.PlacesOfKind(PlaceKind.School);
            var leisure = new List<Place>();
            foreach (var p in _geography.Places)
            {
                switch (p.Kind)
                {
                    case PlaceKind.Shop:
                    case PlaceKind.Restaurant:
                    case PlaceKind.Park:
                    case PlaceKind.Gym:
                    case PlaceKind.Church:
                    case PlaceKind.Nightlife:
                    case PlaceKind.Beach:
                        leisure.Add(p);
                        break;
                }
            }
            leisure.Sort((a, b) => a.Id.CompareTo(b.Id));

            var residences = new List<Place>();
            foreach (var p in _geography.Places)
                if (p.Kind == PlaceKind.Residence || p.Kind == PlaceKind.ApartmentBuilding) residences.Add(p);
            residences.Sort((a, b) => a.Id.CompareTo(b.Id));

            foreach (var home in residences)
            {
                var district = _geography.GetDistrict(home.District);
                var wealth = district != null ? district.Wealth : 0.5f;
                var units = home.Kind == PlaceKind.ApartmentBuilding ? Math.Max(1, (int)Math.Round(home.Capacity / 2.5f * density)) : 1;
                if (home.Kind == PlaceKind.Residence && density < 1f && !rng.Chance(density)) continue;
                // Housing-market vacancy: some homes are empty and on the market (keyed by place, not RNG order).
                if (home.Kind == PlaceKind.Residence && StableHash.Combine(_seed, home.Id.Value, 0x7AC) % 100 < VacantHomePercent) continue;
                for (var u = 0; u < units; u++)
                    CreateHousehold(registry, home, wealth, startDay, rng, workplaces, schools, leisure);
            }

            LinkCoworkersAndNeighbors(registry, startDay, rng);
        }

        private void CreateHousehold(PopulationRegistry registry, Place home, float wealth, long day, DeterministicRandom rng,
            Dictionary<PlaceKind, List<Place>> workplaces, List<Place> schools, List<Place> leisure)
        {
            var surname = rng.Pick(_names.Last);
            var household = new Household { Id = _ids.Next(EntityKind.Household), Surname = surname, Home = home.Id, OwnsHome = home.Kind == PlaceKind.Residence && rng.Chance(0.35 + wealth * 0.5) };

            // Household composition.
            var roll = rng.NextDouble();
            int adults, kids;
            bool couple, elderly = false;
            if (roll < 0.26) { adults = 1; kids = 0; couple = false; }
            else if (roll < 0.50) { adults = 2; kids = 0; couple = true; }
            else if (roll < 0.80) { adults = 2; kids = rng.NextInt(1, 4); couple = true; }
            else if (roll < 0.90) { adults = 1; kids = rng.NextInt(1, 3); couple = false; }
            else if (roll < 0.95) { adults = 2; kids = 0; couple = false; } // roommates
            else { adults = 2; kids = 0; couple = true; elderly = true; }

            var adultRecords = new List<NpcRecord>();
            var firstSex = rng.Chance(0.5) ? Sex.Female : Sex.Male;
            var baseAge = elderly ? rng.NextInt(66, 84) : rng.NextInt(21, 58);
            for (var i = 0; i < adults; i++)
            {
                var sex = i == 0 ? firstSex : couple ? (rng.Chance(0.93) ? Opposite(firstSex) : firstSex) : (rng.Chance(0.5) ? Sex.Female : Sex.Male);
                var age = Math.Max(19, baseAge + (i == 0 ? 0 : rng.NextInt(-5, 6)));
                var last = couple ? (rng.Chance(0.8) ? surname : rng.Pick(_names.Last)) : (i == 0 ? surname : rng.Pick(_names.Last));
                var npc = CreatePerson(sex, age, last, day, rng, household, home, wealth);
                if (!elderly) AssignJob(npc, wealth, day, rng, workplaces);
                else npc.Employment = EmploymentStatus.Retired;
                AssignFavorites(npc, home, rng, leisure);
                adultRecords.Add(npc);
            }
            if (couple && adultRecords.Count == 2)
            {
                var since = day - rng.NextInt(365, 365 * 20);
                adultRecords[0].SetRelationship(adultRecords[1].Id, RelationshipType.Spouse, 0.5f + rng.NextFloat() * 0.4f, 0.9f, since);
                adultRecords[1].SetRelationship(adultRecords[0].Id, RelationshipType.Spouse, 0.5f + rng.NextFloat() * 0.4f, 0.9f, since);
            }
            else if (adultRecords.Count == 2)
            {
                adultRecords[0].SetRelationship(adultRecords[1].Id, RelationshipType.Friend, 0.3f, 0.6f, day - 400);
                adultRecords[1].SetRelationship(adultRecords[0].Id, RelationshipType.Friend, 0.3f, 0.6f, day - 400);
            }

            var youngest = int.MaxValue;
            foreach (var a in adultRecords) youngest = Math.Min(youngest, a.AgeYears(day));
            var kidRecords = new List<NpcRecord>();
            for (var k = 0; k < kids; k++)
            {
                var maxAge = Math.Max(0, Math.Min(17, youngest - 18));
                var age = rng.NextInt(0, maxAge + 1);
                var child = CreatePerson(rng.Chance(0.5) ? Sex.Female : Sex.Male, age, surname, day, rng, household, home, wealth);
                child.Employment = age < 5 ? EmploymentStatus.Child : EmploymentStatus.Student;
                if (age >= 5 && schools.Count > 0) child.School = Nearest(schools, home).Id;
                foreach (var parent in adultRecords)
                {
                    child.SetRelationship(parent.Id, RelationshipType.Parent, 0.8f, 1f, child.BirthDay);
                    parent.SetRelationship(child.Id, RelationshipType.Child, 0.9f, 1f, child.BirthDay);
                }
                foreach (var sibling in kidRecords)
                {
                    child.SetRelationship(sibling.Id, RelationshipType.Sibling, 0.5f, 1f, child.BirthDay);
                    sibling.SetRelationship(child.Id, RelationshipType.Sibling, 0.5f, 1f, child.BirthDay);
                }
                AssignFavorites(child, home, rng, leisure);
                kidRecords.Add(child);
            }

            registry.Add(household);
            foreach (var n in adultRecords) registry.Add(n);
            foreach (var n in kidRecords) registry.Add(n);
        }

        private NpcRecord CreatePerson(Sex sex, int age, string lastName, long day, DeterministicRandom rng, Household household, Place home, float wealth)
        {
            var npc = new NpcRecord
            {
                Id = _ids.Next(EntityKind.Npc),
                FirstName = _names.PickFirst(rng, sex, lastName),
                LastName = lastName,
                Sex = sex,
                Presentation = rng.Chance(0.04) ? GenderPresentation.Androgynous : sex == Sex.Female ? GenderPresentation.Feminine : GenderPresentation.Masculine,
                BirthDay = day - (long)(age * 365.25) - rng.NextInt(0, 365),
                AppearanceSeed = rng.NextULong(),
                SkinTone = rng.NextInt(0, 10),
                HeightCm = age < 18 ? 60f + age * 6.5f : (sex == Sex.Female ? 163f : 176f) + (float)rng.NextGaussian() * 7f,
                Household = household.Id,
                Home = home.Id,
                LastSimulatedDay = day,
                Health = Math.Min(1f, 0.75f + rng.NextFloat() * 0.25f),
                Personality = new Personality
                {
                    Openness = Trait(rng), Conscientiousness = Trait(rng), Extraversion = Trait(rng),
                    Agreeableness = Trait(rng), Neuroticism = Trait(rng),
                },
            };
            npc.CriminalPropensity = Math.Max(0f, Math.Min(1f, 0.05f + (1f - npc.Personality.Agreeableness) * 0.1f + (1f - wealth) * 0.08f + (float)rng.NextGaussian() * 0.04f));
            household.Members.Add(npc.Id);
            return npc;
        }

        private void AssignJob(NpcRecord npc, float wealth, long day, DeterministicRandom rng, Dictionary<PlaceKind, List<Place>> workplaces)
        {
            // Education correlates with district wealth but is never determined by it.
            var eduRoll = rng.NextDouble() + wealth * 0.45;
            npc.Education = eduRoll < 0.12 ? EducationLevel.None
                : eduRoll < 0.55 ? EducationLevel.HighSchool
                : eduRoll < 0.75 ? EducationLevel.Vocational
                : eduRoll < 1.1 ? EducationLevel.Bachelors
                : eduRoll < 1.3 ? EducationLevel.Masters
                : EducationLevel.Doctorate;

            if (rng.Chance(0.06))
            {
                npc.Employment = EmploymentStatus.Unemployed;
                npc.SavingsCents = rng.NextInt(0, 300000);
                return;
            }

            var candidates = new List<OccupationDefinition>();
            var weights = new List<double>();
            foreach (var occ in _occupations.All)
            {
                if (npc.Education < occ.MinEducation) continue;
                var available = false;
                foreach (var k in occ.WorkplaceKinds)
                    if (workplaces.TryGetValue(k, out var l) && l.Count > 0) { available = true; break; }
                if (!available) continue;
                candidates.Add(occ);
                // Prefer jobs that use the person's education.
                weights.Add(occ.DemandWeight * (1.0 + 0.5 * (int)occ.MinEducation * ((int)npc.Education >= (int)occ.MinEducation ? 1 : 0)));
            }
            var index = rng.PickWeighted(weights);
            if (index < 0)
            {
                npc.Employment = EmploymentStatus.Unemployed;
                return;
            }
            var job = candidates[index];
            var places = new List<Place>();
            foreach (var k in job.WorkplaceKinds)
                if (workplaces.TryGetValue(k, out var l)) places.AddRange(l);
            var workplace = places[rng.NextInt(0, places.Count)];

            npc.Employment = EmploymentStatus.Employed;
            npc.OccupationId = job.Id;
            npc.Workplace = workplace.Id;
            npc.JobStartDay = day - rng.NextInt(0, 365 * 8);
            npc.JobPerformance = 0.35f + rng.NextFloat() * 0.5f;
            var t = Math.Min(1.0, Math.Max(0.0, rng.NextDouble() * 0.7 + wealth * 0.3));
            npc.AnnualSalaryCents = job.MinSalaryCents + (long)((job.MaxSalaryCents - job.MinSalaryCents) * t);
            npc.SavingsCents = (long)(npc.AnnualSalaryCents * (0.02 + rng.NextDouble() * 0.4 * (0.3 + wealth)));
            npc.DebtCents = rng.Chance(0.4) ? (long)(npc.AnnualSalaryCents * rng.NextDouble() * 0.3) : 0;
        }

        private static void AssignFavorites(NpcRecord npc, Place home, DeterministicRandom rng, List<Place> leisure)
        {
            if (leisure.Count == 0) return;
            // Mostly nearby places, occasionally somewhere across town.
            var sorted = new List<Place>(leisure);
            sorted.Sort((a, b) => WorldPosition.DistanceSquaredXZ(a.Position, home.Position).CompareTo(WorldPosition.DistanceSquaredXZ(b.Position, home.Position)));
            var count = rng.NextInt(2, 5);
            for (var i = 0; i < count; i++)
            {
                var pick = rng.Chance(0.8) ? sorted[Math.Min(sorted.Count - 1, rng.NextInt(0, Math.Min(6, sorted.Count)))] : sorted[rng.NextInt(0, sorted.Count)];
                if (!npc.FavoritePlaces.Contains(pick.Id)) npc.FavoritePlaces.Add(pick.Id);
            }
        }

        private static void LinkCoworkersAndNeighbors(PopulationRegistry registry, long day, DeterministicRandom rng)
        {
            var byWorkplace = new Dictionary<EntityId, List<NpcRecord>>();
            var byHome = new Dictionary<EntityId, List<NpcRecord>>();
            foreach (var npc in registry.Ordered)
            {
                if (npc.Workplace.IsValid) Bucket(byWorkplace, npc.Workplace, npc);
                if (npc.AgeYears(day) >= 18) Bucket(byHome, npc.Home, npc);
            }
            LinkWithin(byWorkplace, RelationshipType.Coworker, day, rng);
            LinkWithin(byHome, RelationshipType.Neighbor, day, rng);
        }

        private static void LinkWithin(Dictionary<EntityId, List<NpcRecord>> buckets, RelationshipType type, long day, DeterministicRandom rng)
        {
            var keys = new List<EntityId>(buckets.Keys);
            keys.Sort();
            foreach (var key in keys)
            {
                var list = buckets[key];
                if (list.Count < 2) continue;
                foreach (var a in list)
                {
                    var links = Math.Min(list.Count - 1, rng.NextInt(1, 4));
                    for (var i = 0; i < links; i++)
                    {
                        var b = list[rng.NextInt(0, list.Count)];
                        if (b == a || a.FindRelationship(b.Id) != null) continue;
                        var affinity = (float)(rng.NextGaussian() * 0.3);
                        a.SetRelationship(b.Id, type, affinity, 0.3f, day - rng.NextInt(30, 2000));
                        b.SetRelationship(a.Id, type, affinity, 0.3f, day - rng.NextInt(30, 2000));
                    }
                }
            }
        }

        private static void Bucket(Dictionary<EntityId, List<NpcRecord>> map, EntityId key, NpcRecord npc)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<NpcRecord>();
                map.Add(key, list);
            }
            list.Add(npc);
        }

        private static Place Nearest(List<Place> places, Place from)
        {
            Place best = places[0];
            var bestDist = float.MaxValue;
            foreach (var p in places)
            {
                var d = WorldPosition.DistanceSquaredXZ(p.Position, from.Position);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }

        private static float Trait(DeterministicRandom rng) => Math.Max(0f, Math.Min(1f, 0.5f + (float)rng.NextGaussian() * 0.18f));
        private static Sex Opposite(Sex s) => s == Sex.Female ? Sex.Male : Sex.Female;
    }
}
