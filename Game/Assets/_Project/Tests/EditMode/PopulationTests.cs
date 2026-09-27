using System.Collections.Generic;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using HeroGame.Core.World;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class PopulationTests
    {
        private static World NewWorld(string id = "test")
        {
            return WorldGenerator.Create(id, TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
        }

        [Test]
        public void Generator_IsDeterministicForSameSeed()
        {
            var a = NewWorld("same");
            var b = NewWorld("same");
            Assert.AreEqual(a.Population.Count, b.Population.Count);
            Assert.Greater(a.Population.Count, 200);
            for (var i = 0; i < a.Population.Ordered.Count; i++)
            {
                var x = a.Population.Ordered[i];
                var y = b.Population.Ordered[i];
                Assert.AreEqual(x.Id, y.Id);
                Assert.AreEqual(x.FullName, y.FullName);
                Assert.AreEqual(x.Workplace, y.Workplace);
                Assert.AreEqual(x.AnnualSalaryCents, y.AnnualSalaryCents);
            }
        }

        [Test]
        public void Generator_DifferentServersGetDifferentPeople()
        {
            var a = NewWorld("server-a");
            var b = NewWorld("server-b");
            var same = 0;
            var n = System.Math.Min(a.Population.Ordered.Count, b.Population.Ordered.Count);
            for (var i = 0; i < n; i++) if (a.Population.Ordered[i].FullName == b.Population.Ordered[i].FullName) same++;
            Assert.Less(same, n / 4);
        }

        [Test]
        public void Households_HaveCoherentFamilies()
        {
            var world = NewWorld();
            var day = world.Today;
            foreach (var h in world.Population.Households)
            {
                Assert.Greater(h.Members.Count, 0);
                foreach (var memberId in h.Members)
                {
                    var npc = world.Population.Get(memberId);
                    Assert.IsNotNull(npc);
                    Assert.AreEqual(h.Id, npc.Household);
                    Assert.AreEqual(h.Home, npc.Home);
                    if (npc.AgeYears(day) < 18)
                    {
                        Assert.IsNotNull(npc.FindRelationship(RelationshipType.Parent), npc.FullName + " is a minor without a parent");
                        Assert.AreNotEqual(EmploymentStatus.Employed, npc.Employment);
                    }
                }
            }
        }

        [Test]
        public void Schedule_IsDeterministicAndPutsWorkersAtWork()
        {
            var world = NewWorld();
            var tuesday = GameDateTime.FromCalendar(2030, 5, 7, 0, 0);
            Assert.AreEqual(System.DayOfWeek.Tuesday, tuesday.DayOfWeek);
            var checkedWorkers = 0;
            foreach (var npc in world.Population.Ordered)
            {
                if (npc.Employment != EmploymentStatus.Employed) continue;
                var occ = world.Occupations.Get(npc.OccupationId);
                if (occ == null || !occ.WorksOn(System.DayOfWeek.Tuesday) || occ.ShiftStartMinute + occ.ShiftLengthMinutes > 1440) continue;
                var midShift = tuesday.AddMinutes(occ.ShiftStartMinute + occ.ShiftLengthMinutes / 2);
                var a = world.Schedules.Resolve(npc, midShift);
                var b = world.Schedules.Resolve(npc, midShift);
                Assert.AreEqual(a.Activity, b.Activity);
                Assert.AreEqual(a.Place, b.Place);
                Assert.AreEqual(ActivityKind.Working, a.Activity, npc.FullName + " (" + npc.OccupationId + ")");
                Assert.AreEqual(npc.Workplace, a.Place);
                checkedWorkers++;
            }
            Assert.Greater(checkedWorkers, 20);
        }

        [Test]
        public void Schedule_EveryoneSleepsAtHomeAt3am()
        {
            var world = NewWorld();
            var t = GameDateTime.FromCalendar(2030, 5, 8, 3, 0);
            var atHome = 0;
            var total = 0;
            foreach (var npc in world.Population.Ordered)
            {
                total++;
                var s = world.Schedules.Resolve(npc, t);
                if (s.Place == npc.Home) atHome++;
            }
            // Night-shift workers (nurses, plant operators, guards, bartenders…) are the exception.
            Assert.Greater(atHome, total * 0.8);
        }

        [Test]
        public void CatchUp_InOneStepEqualsManySmallSteps()
        {
            var a = NewWorld("catchup");
            var b = NewWorld("catchup");
            var ctxA = a.CreateLifeContext();
            var ctxB = b.CreateLifeContext();
            var start = a.Today;
            for (var i = 0; i < a.Population.Ordered.Count; i++)
            {
                var x = a.Population.Ordered[i];
                var y = b.Population.Ordered[i];
                NpcLifeSimulator.CatchUp(x, start + 120, ctxA);
                for (var d = 1; d <= 120; d++) NpcLifeSimulator.CatchUp(y, start + d, ctxB);
                Assert.AreEqual(x.SavingsCents, y.SavingsCents, x.FullName);
                Assert.AreEqual(x.DebtCents, y.DebtCents);
                Assert.AreEqual(x.Employment, y.Employment);
                Assert.AreEqual(x.OccupationId, y.OccupationId);
                Assert.AreEqual(x.Alive, y.Alive);
                Assert.AreEqual(x.History.Count, y.History.Count);
            }
        }

        [Test]
        public void Director_MaterializesTheSamePersistentPeople()
        {
            var world = NewWorld();
            var market = FindPlace(world, "Lupe's Corner Market");
            var observers = new List<WorldPosition> { market.Position };
            var t = GameDateTime.FromCalendar(2030, 5, 7, 12, 0);
            var first = world.Director.Evaluate(observers, t);
            var second = world.Director.Evaluate(observers, t);
            Assert.Greater(first.Count, 0);
            Assert.AreEqual(first.Count, second.Count);
            for (var i = 0; i < first.Count; i++) Assert.AreEqual(first[i].Npc, second[i].Npc);
            var full = 0;
            foreach (var r in first) if (r.Tier == SimulationTier.Full) full++;
            Assert.LessOrEqual(full, world.Director.Settings.MaxFull);
        }

        [Test]
        public void Schedule_ValidityWindowIsExact()
        {
            var world = NewWorld("validity");
            var start = GameDateTime.FromCalendar(2030, 5, 6, 0, 0);
            var checkedWindows = 0;
            foreach (var npc in world.Population.Ordered)
            {
                if (npc.Id.Sequence % 7 != 0) continue;
                for (var t = start; t < start.AddDays(3); t = t.AddMinutes(53))
                {
                    var a = world.Schedules.Resolve(npc, t, out var until);
                    Assert.Greater(until.TotalSeconds, t.TotalSeconds);
                    Assert.LessOrEqual(until.TotalSeconds, t.StartOfDay.AddDays(1).TotalSeconds);
                    // Same answer anywhere inside the window…
                    for (var probe = t; probe < until; probe = probe.AddMinutes(11))
                    {
                        var b = world.Schedules.Resolve(npc, probe);
                        Assert.AreEqual(a.Activity, b.Activity, npc.FullName + " at " + probe);
                        Assert.AreEqual(a.Place, b.Place);
                    }
                    checkedWindows++;
                }
            }
            Assert.Greater(checkedWindows, 1000);
        }

        [Test]
        public void LocationIndex_AgreesWithDirectResolution()
        {
            var world = NewWorld("index");
            var index = new NpcLocationIndex(world.Population, world.Geography, world.Schedules);
            var t = GameDateTime.FromCalendar(2030, 5, 7, 5, 0);
            var mismatches = 0;
            for (var step = 0; step < 60; step++)
            {
                t = t.AddMinutes(17);
                index.Update(t);
                foreach (var npc in world.Population.Ordered)
                {
                    var direct = world.Schedules.Resolve(npc, t);
                    var has = index.TryGet(npc.Id, t, out var indexed, out var pos);
                    var directHas = world.Director.TryGetPosition(direct, out var directPos);
                    if (direct.Activity != indexed.Activity || direct.Place != indexed.Place || has != directHas) mismatches++;
                    else if (has && WorldPosition.DistanceXZ(pos, directPos) > 60f) mismatches++; // commute interpolation tolerance
                }
            }
            Assert.AreEqual(0, mismatches);
            Assert.Less(index.ResolvesLastUpdate, world.Population.Count / 2, "only changed schedules are re-resolved");
        }

        [Test]
        public void Memory_IsBoundedAndKeepsSignificantPeople()
        {
            var npc = new NpcRecord { Id = EntityId.Create(EntityKind.Npc, 1) };
            var important = EntityId.Create(EntityKind.Character, 999);
            var m = npc.MemoryOf(important, true, 1);
            m.Flags = MemoryFlags.WasRescued;
            m.Trust = 0.9f;
            for (ulong i = 1; i <= 100; i++) npc.MemoryOf(EntityId.Create(EntityKind.Character, i), true, (long)i);
            Assert.LessOrEqual(npc.Memories.Count, NpcRecord.MaxMemories);
            Assert.IsNotNull(npc.MemoryOf(important, false, 0));
        }

        private static Place FindPlace(World world, string name)
        {
            foreach (var p in world.Geography.Places) if (p.Name == name) return p;
            Assert.Fail("Place not found: " + name);
            return null;
        }
    }
}
