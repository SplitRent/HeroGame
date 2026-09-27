using System;
using System.Collections.Generic;
using HeroGame.Core.Foundation;

namespace HeroGame.Core.Population
{
    /// <summary>
    /// World-level daily pass for events that involve more than one NPC (GDD §15): relationships
    /// forming, marriages, divorces and births. Iterates in stable id order with a per-day seed,
    /// so results are deterministic and independent of which NPCs happen to be on screen.
    /// </summary>
    public sealed class SocialSimulator
    {
        private readonly ulong _worldSeed;
        private readonly IdAllocator _ids;
        private readonly NameTables _names;

        public event Action<NpcRecord, LifeEvent> Notable;

        public SocialSimulator(ulong worldSeed, IdAllocator ids, NameTables names)
        {
            _worldSeed = worldSeed;
            _ids = ids;
            _names = names.IsUsable ? names : NameTables.Fallback();
        }

        public void DailyPass(PopulationRegistry population, long day)
        {
            var rng = DeterministicRandom.For(_worldSeed, 0x50C1A1, (ulong)day);
            var ordered = population.Ordered;
            var count = ordered.Count; // children born today are not processed until tomorrow

            // Singles bucketed by workplace/home for plausible meeting pools.
            var singlesByPool = new Dictionary<EntityId, List<NpcRecord>>();
            for (var i = 0; i < count; i++)
            {
                var npc = ordered[i];
                if (!npc.Alive) continue;
                var age = npc.AgeYears(day);
                if (age < 20 || age > 65 || HasPartner(npc)) continue;
                var pool = npc.Workplace.IsValid ? npc.Workplace : npc.Home;
                if (!singlesByPool.TryGetValue(pool, out var list))
                {
                    list = new List<NpcRecord>();
                    singlesByPool.Add(pool, list);
                }
                list.Add(npc);
            }

            // New partnerships.
            foreach (var pool in SortedKeys(singlesByPool))
            {
                var list = singlesByPool[pool];
                for (var i = 0; i < list.Count; i++)
                {
                    var a = list[i];
                    if (HasPartner(a) || !rng.Chance(0.0015 + 0.002 * a.Personality.Extraversion)) continue;
                    for (var tries = 0; tries < 3; tries++)
                    {
                        var b = list[rng.NextInt(0, list.Count)];
                        if (b == a || HasPartner(b) || Math.Abs(a.AgeYears(day) - b.AgeYears(day)) > 12 || AreRelatives(a, b)) continue;
                        a.SetRelationship(b.Id, RelationshipType.Partner, 0.6f, 0.4f, day);
                        b.SetRelationship(a.Id, RelationshipType.Partner, 0.6f, 0.4f, day);
                        Emit(a, day, "partnered", a.FullName + " started dating " + b.FullName + ".");
                        break;
                    }
                }
            }

            for (var i = 0; i < count; i++)
            {
                var a = ordered[i];
                if (!a.Alive) continue;
                var partnerRel = a.FindRelationship(RelationshipType.Partner) ?? a.FindRelationship(RelationshipType.Spouse);
                if (partnerRel == null || partnerRel.Other.Value < a.Id.Value) continue; // handle each couple once
                var b = population.Get(partnerRel.Other);
                if (b == null) continue;
                if (!b.Alive)
                {
                    partnerRel.Type = RelationshipType.ExPartner;
                    continue;
                }
                var years = (day - partnerRel.SinceDay) / 365.0;
                var harmony = (a.Personality.Agreeableness + b.Personality.Agreeableness) * 0.5f - (a.Personality.Neuroticism + b.Personality.Neuroticism) * 0.25f;

                if (partnerRel.Type == RelationshipType.Partner)
                {
                    if (years > 1 && rng.Chance((0.25 + harmony) / 365.0 / 2.0)) Marry(a, b, day, population);
                    else if (rng.Chance((0.35 - harmony * 0.3) / 365.0)) BreakUp(a, b, day, RelationshipType.ExPartner, "broke up");
                }
                else if (partnerRel.Type == RelationshipType.Spouse)
                {
                    if (rng.Chance(Math.Max(0.002, 0.02 - harmony * 0.02) / 365.0)) BreakUp(a, b, day, RelationshipType.ExPartner, "divorced");
                    else TryHaveChild(a, b, day, rng, population);
                }
            }
        }

        private void Marry(NpcRecord a, NpcRecord b, long day, PopulationRegistry population)
        {
            a.SetRelationship(b.Id, RelationshipType.Spouse, 0.8f, 0.8f, day);
            b.SetRelationship(a.Id, RelationshipType.Spouse, 0.8f, 0.8f, day);
            // The couple moves into a's household (simplification; housing market moves are Phase 19).
            var home = population.GetHousehold(a.Household);
            var old = population.GetHousehold(b.Household);
            if (home != null && old != null && home != old)
            {
                old.Members.Remove(b.Id);
                home.Members.Add(b.Id);
                b.Household = home.Id;
                b.Home = a.Home;
                population.Reindex(b);
            }
            Emit(a, day, "married", a.FullName + " married " + b.FullName + ".");
            Emit(b, day, "married", b.FullName + " married " + a.FullName + ".");
        }

        private void BreakUp(NpcRecord a, NpcRecord b, long day, RelationshipType becomes, string verb)
        {
            a.SetRelationship(b.Id, becomes, -0.2f, 0.7f, day);
            b.SetRelationship(a.Id, becomes, -0.2f, 0.7f, day);
            Emit(a, day, verb.Replace(' ', '_'), a.FullName + " and " + b.FullName + " " + verb + ".");
        }

        private void TryHaveChild(NpcRecord a, NpcRecord b, long day, DeterministicRandom rng, PopulationRegistry population)
        {
            var mother = a.Sex == Sex.Female ? a : b.Sex == Sex.Female ? b : null;
            if (mother == null) return; // adoption path is future work
            var age = mother.AgeYears(day);
            if (age < 20 || age > 42) return;
            var children = 0;
            foreach (var r in mother.Relationships) if (r.Type == RelationshipType.Child) children++;
            if (children >= 4 || !rng.Chance(1.0 / (365.0 * (3 + children * 2)))) return;

            var household = population.GetHousehold(mother.Household);
            var sex = rng.Chance(0.5) ? Sex.Female : Sex.Male;
            var child = new NpcRecord
            {
                Id = _ids.Next(EntityKind.Npc),
                FirstName = _names.PickFirst(rng, sex, household != null ? household.Surname : mother.LastName),
                LastName = household != null ? household.Surname : mother.LastName,
                BirthDay = day,
                Sex = sex,
                Presentation = sex == Sex.Female ? GenderPresentation.Feminine : GenderPresentation.Masculine,
                AppearanceSeed = StableHash.Combine(mother.AppearanceSeed, a.AppearanceSeed ^ b.AppearanceSeed, (ulong)day),
                HeightCm = 50f,
                SkinTone = rng.Chance(0.5) ? a.SkinTone : b.SkinTone,
                Household = mother.Household,
                Home = mother.Home,
                Employment = EmploymentStatus.Child,
                Education = EducationLevel.None,
                LastSimulatedDay = day,
                Personality = new Personality
                {
                    Openness = rng.NextFloat(), Conscientiousness = rng.NextFloat(), Extraversion = rng.NextFloat(),
                    Agreeableness = rng.NextFloat(), Neuroticism = rng.NextFloat(),
                },
            };
            child.SetRelationship(a.Id, RelationshipType.Parent, 0.9f, 1f, day);
            child.SetRelationship(b.Id, RelationshipType.Parent, 0.9f, 1f, day);
            a.SetRelationship(child.Id, RelationshipType.Child, 0.95f, 1f, day);
            b.SetRelationship(child.Id, RelationshipType.Child, 0.95f, 1f, day);
            household?.Members.Add(child.Id);
            population.Add(child);
            Emit(mother, day, "birth", a.FullName + " and " + b.FullName + " welcomed " + child.FullName + ".");
        }

        private static bool HasPartner(NpcRecord n)
        {
            foreach (var r in n.Relationships)
                if (r.Type == RelationshipType.Partner || r.Type == RelationshipType.Spouse) return true;
            return false;
        }

        private static bool AreRelatives(NpcRecord a, NpcRecord b)
        {
            var r = a.FindRelationship(b.Id);
            return r != null && (r.Type == RelationshipType.Parent || r.Type == RelationshipType.Child || r.Type == RelationshipType.Sibling);
        }

        private void Emit(NpcRecord npc, long day, string kind, string summary)
        {
            npc.AddHistory(day, kind, summary);
            Notable?.Invoke(npc, npc.History[npc.History.Count - 1]);
        }

        private static List<EntityId> SortedKeys(Dictionary<EntityId, List<NpcRecord>> map)
        {
            var keys = new List<EntityId>(map.Keys);
            keys.Sort();
            return keys;
        }
    }
}
