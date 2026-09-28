using System;
using System.Linq;
using HeroGame.Core.Population;
using HeroGame.Core.Simulation;
using HeroGame.Core.World;

namespace HeroGame.WorldHost
{
    /// <summary>Text versions of the World/NPC/Server inspectors (GDD §151–153) for headless servers.</summary>
    internal static class Inspector
    {
        public static void Economy(World world)
        {
            var m = world.Macro;
            Console.WriteLine("Macro: cycle " + m.CycleIndex.ToString("0.000") + ", momentum " + m.Momentum.ToString("+0.0000;-0.0000") + ", price level " + m.PriceLevel.ToString("0.0000") +
                              ", inflation " + (m.AnnualInflation * 100).ToString("0.00") + "%, recession " + m.InRecession);
            Console.WriteLine("Accounts by kind:");
            foreach (var g in world.Ledger.Accounts.GroupBy(a => a.Kind).OrderBy(g => g.Key))
                Console.WriteLine("  " + g.Key.ToString().PadRight(18) + g.Count().ToString().PadLeft(6) + "  total " + new HeroGame.Core.Foundation.Money(g.Sum(a => a.BalanceCents)));
            var npcs = world.Population.Ordered.Where(n => n.Alive && n.AgeYears(world.Today) >= 18).ToList();
            if (npcs.Count == 0) return;
            var savings = npcs.Select(n => n.SavingsCents).OrderBy(v => v).ToList();
            Console.WriteLine("NPC household finances (adults): median savings " + new HeroGame.Core.Foundation.Money(savings[savings.Count / 2]) +
                              ", in debt " + npcs.Count(n => n.DebtCents > 0) + "/" + npcs.Count);
        }

        public static void Npc(World world, string name)
        {
            var matches = world.Population.FindByName(name);
            if (matches.Count == 0)
            {
                Console.WriteLine("No NPC matching '" + name + "'.");
                return;
            }
            foreach (var n in matches.Take(3))
            {
                var day = world.Today;
                var home = world.Geography.GetPlace(n.Home);
                var work = world.Geography.GetPlace(n.Workplace);
                var occ = world.Occupations.Get(n.OccupationId);
                var now = world.Schedules.Resolve(n, world.Clock.Now);
                var at = world.Geography.GetPlace(now.Place);
                Console.WriteLine("── " + n.FullName + " (" + n.Id + ")" + (n.Alive ? "" : " — deceased"));
                Console.WriteLine("   Age " + n.AgeYears(day) + ", " + n.Presentation + ", " + n.HeightCm.ToString("0") + " cm · education " + n.Education);
                Console.WriteLine("   Home       " + (home != null ? home.Name : "-"));
                Console.WriteLine("   Work       " + n.Employment + (occ != null ? " — " + occ.Title + " at " + (work != null ? work.Name : "?") + ", " + new HeroGame.Core.Foundation.Money(n.AnnualSalaryCents) + "/yr" : ""));
                Console.WriteLine("   Right now  " + now.Activity + (at != null ? " @ " + at.Name : ""));
                Console.WriteLine("   Finances   savings " + new HeroGame.Core.Foundation.Money(n.SavingsCents) + ", debt " + new HeroGame.Core.Foundation.Money(n.DebtCents));
                Console.WriteLine("   Personality O" + n.Personality.Openness.ToString("0.00") + " C" + n.Personality.Conscientiousness.ToString("0.00") +
                                  " E" + n.Personality.Extraversion.ToString("0.00") + " A" + n.Personality.Agreeableness.ToString("0.00") + " N" + n.Personality.Neuroticism.ToString("0.00"));
                foreach (var r in n.Relationships.Where(r => r.Type != RelationshipType.Acquaintance).Take(8))
                {
                    var other = world.Population.Get(r.Other);
                    Console.WriteLine("   " + r.Type.ToString().PadRight(11) + (other != null ? other.FullName : r.Other.ToString()) + " (affinity " + r.Affinity.ToString("+0.00;-0.00") + ")");
                }
                if (n.Powers != null && n.Powers.Powers.Count > 0)
                    foreach (var p in n.Powers.Powers) Console.WriteLine("   Anomalous  " + p.Definition.Describe() + " — " + p.Stage);
                foreach (var h in n.History.Skip(Math.Max(0, n.History.Count - 6))) Console.WriteLine("   day " + h.Day + ": " + h.Summary);
            }
        }

        public static void Businesses(World world)
        {
            Console.WriteLine("Business".PadRight(34) + "Staff  Rep   Last day revenue   profit    Lifetime profit  Owner");
            foreach (var b in world.Businesses.Values.OrderByDescending(b => b.LifetimeProfitCents))
            {
                var last = b.Reports.Count > 0 ? b.Reports[b.Reports.Count - 1] : null;
                Console.WriteLine(b.Name.PadRight(34) + b.Staff.ToString().PadLeft(5) + "  " + b.Reputation.ToString("0.00") + "  " +
                                  (last != null ? new HeroGame.Core.Foundation.Money(last.RevenueCents).ToString().PadLeft(14) + new HeroGame.Core.Foundation.Money(last.ProfitCents).ToString().PadLeft(12) : "".PadLeft(26)) +
                                  new HeroGame.Core.Foundation.Money(b.LifetimeProfitCents).ToString().PadLeft(18) + "  " + world.Ownership.OwnerOf(b.Id));
            }
        }

        public static void History(World world)
        {
            foreach (var r in world.History.Major) Console.WriteLine("★ day " + r.Day + " [" + r.Category + "] " + r.Headline);
            foreach (var r in world.History.Recent.Skip(Math.Max(0, world.History.Recent.Count - 25)))
                if (r.Importance < ServerHistory.MajorThreshold) Console.WriteLine("  day " + r.Day + " [" + r.Category + "] " + r.Headline);
        }

        public static void News(World world)
        {
            foreach (var a in NewsDesk.Edition(world.History, world.Today - 3, world.Config.Identity.NewsOrganizations, 10))
                Console.WriteLine(a.Outlet + " — " + a.Headline + Environment.NewLine + "    " + a.Body);
        }

        public static void Districts(World world)
        {
            foreach (var d in world.Geography.Districts)
            {
                var residents = world.Population.Ordered.Count(n => n.Alive && world.Geography.GetPlace(n.Home)?.District == d.Id);
                Console.WriteLine(d.Name.PadRight(14) + d.Type.ToString().PadRight(11) + "wealth " + d.Wealth.ToString("0.00") + "  residents " + residents.ToString().PadLeft(5) +
                                  "  places " + world.Geography.PlacesIn(d.Id).Count.ToString().PadLeft(4) + "  crime " + d.CrimeBaseline.ToString("0.00") + "  trust " + d.PoliceTrust.ToString("0.00"));
                Console.WriteLine("    " + d.Description);
            }
        }
    }
}
