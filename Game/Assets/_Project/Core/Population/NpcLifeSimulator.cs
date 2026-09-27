using System;
using System.Collections.Generic;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.World;

namespace HeroGame.Core.Population
{
    /// <summary>Everything the life simulator reads. Built once per day by the population system.</summary>
    public sealed class LifeSimContext
    {
        public ulong WorldSeed;
        public OccupationTable Occupations;
        public Geography Geography;
        public MacroEconomyState Macro;
        public float IncomeTaxRate = 0.12f;
        public float WageMultiplier = 1f;
        public float CostOfLivingMultiplier = 1f;
        /// <summary>Places accepting new hires, by kind (built from geography).</summary>
        public Dictionary<PlaceKind, List<Place>> WorkplacesByKind = new Dictionary<PlaceKind, List<Place>>();
        /// <summary>Raised for events worth surfacing to history/news (death, layoff wave, etc.).</summary>
        public Action<NpcRecord, LifeEvent> Notable;

        public static Dictionary<PlaceKind, List<Place>> IndexWorkplaces(Geography geography)
        {
            var map = new Dictionary<PlaceKind, List<Place>>();
            foreach (var p in geography.Places)
            {
                if (p.Kind == PlaceKind.Residence || p.Kind == PlaceKind.ApartmentBuilding || p.Kind == PlaceKind.Vacant) continue;
                if (!map.TryGetValue(p.Kind, out var list))
                {
                    list = new List<Place>();
                    map.Add(p.Kind, list);
                }
                list.Add(p);
            }
            foreach (var list in map.Values) list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return map;
        }
    }

    /// <summary>
    /// Advances one NPC's life by whole days using aggregate rules (GDD §14–15): income, living
    /// costs, employment changes, promotions, retirement, health and mortality. It never needs the
    /// NPC to be on screen. Randomness is keyed per (NPC, day), so a month simulated in one call is
    /// identical to thirty single-day calls.
    /// </summary>
    public static class NpcLifeSimulator
    {
        /// <summary>Simulates every day after <see cref="NpcRecord.LastSimulatedDay"/> up to and including <paramref name="targetDay"/>.</summary>
        public static int CatchUp(NpcRecord npc, long targetDay, LifeSimContext ctx)
        {
            if (npc.LastSimulatedDay == long.MinValue) npc.LastSimulatedDay = targetDay - 1;
            var simulated = 0;
            while (npc.LastSimulatedDay < targetDay)
            {
                SimulateDay(npc, npc.LastSimulatedDay + 1, ctx);
                npc.LastSimulatedDay++;
                simulated++;
            }
            return simulated;
        }

        public static void SimulateDay(NpcRecord npc, long day, LifeSimContext ctx)
        {
            if (!npc.Alive) return;
            var rng = DeterministicRandom.For(ctx.WorldSeed, npc.Id.Value, (ulong)day, 0x11FE);
            var age = npc.AgeYears(day);

            if (npc.OverrideUntilDay != 0 && npc.OverrideUntilDay <= day)
            {
                npc.OverrideUntilDay = 0;
                npc.OverridePlace = EntityId.None;
            }

            // --- Mortality (Gompertz hazard, per day). Important NPCs are protected from random death.
            if (!npc.IsImportant && age >= 30)
            {
                var annualHazard = 0.00003 * Math.Exp(0.089 * age) * (1.6 - 0.6 * npc.Health);
                if (rng.Chance(annualHazard / 365.0))
                {
                    npc.Alive = false;
                    npc.DeathDay = day;
                    npc.CurrentActivity = ActivityKind.Deceased;
                    Record(npc, day, "death", npc.FullName + " passed away at " + age + ".", ctx);
                    return;
                }
            }

            // --- Health drift and occasional illness.
            npc.Health = Clamp01(npc.Health + (float)(rng.NextGaussian() * 0.004) + (npc.Health < 0.85f ? 0.003f : 0f) - Math.Max(0, age - 55) * 0.00002f);
            if (rng.Chance(0.0008 + Math.Max(0, age - 60) * 0.00005))
            {
                npc.Health = Math.Max(0.2f, npc.Health - 0.25f);
                var hospitals = ctx.WorkplacesByKind.TryGetValue(PlaceKind.Hospital, out var hs) ? hs : null;
                if (hospitals != null && hospitals.Count > 0 && rng.Chance(0.3))
                {
                    npc.OverrideActivity = ActivityKind.Hospitalized;
                    npc.OverridePlace = hospitals[rng.NextInt(0, hospitals.Count)].Id;
                    npc.OverrideUntilDay = day + rng.NextInt(1, 6);
                    Record(npc, day, "hospitalized", npc.FullName + " was admitted to hospital.", ctx);
                }
            }

            // --- Life stage transitions.
            if (npc.Employment == EmploymentStatus.Child && age >= 5) npc.Employment = EmploymentStatus.Student;
            if (npc.Employment == EmploymentStatus.Student && age >= 18 && rng.Chance(1.0 / 60.0))
            {
                npc.Education = npc.Education < EducationLevel.HighSchool ? EducationLevel.HighSchool : npc.Education;
                npc.Employment = EmploymentStatus.Unemployed;
                npc.School = EntityId.None;
                Record(npc, day, "graduated", npc.FullName + " graduated from high school.", ctx);
            }
            if (npc.Employment == EmploymentStatus.Employed && age >= 62 && rng.Chance((age - 61) / 900.0))
            {
                npc.Employment = EmploymentStatus.Retired;
                npc.Workplace = EntityId.None;
                Record(npc, day, "retired", npc.FullName + " retired.", ctx);
            }

            // --- Employment.
            var macro = ctx.Macro;
            var unemploymentPressure = macro != null ? (macro.UnemploymentRate - 0.045) * 12.0 : 0.0;
            if (npc.Employment == EmploymentStatus.Employed)
            {
                var occ = ctx.Occupations.Get(npc.OccupationId);
                // Separations into unemployment (layoffs, firings, quits without a new job lined up).
                // LayoffRisk is the occupation's relative risk; ×3.5 calibrates to ~12–15 %/yr overall,
                // which with the finding rate below yields ~4–5 % unemployment in a normal economy.
                var layoffAnnual = (occ != null ? occ.LayoffRisk : 0.04) * 3.5 * Math.Max(0.3, 1.0 + unemploymentPressure) * (1.3 - npc.JobPerformance * 0.6);
                if (rng.Chance(layoffAnnual / 365.0))
                {
                    Record(npc, day, "laid_off", npc.FullName + " lost their job as " + (occ != null ? occ.Title : npc.OccupationId) + ".", ctx);
                    npc.Employment = EmploymentStatus.Unemployed;
                    npc.Workplace = EntityId.None;
                }
                else
                {
                    npc.JobPerformance = Clamp01(npc.JobPerformance + (float)(rng.NextGaussian() * 0.01) + (npc.Personality.Conscientiousness - 0.5f) * 0.002f);
                    var tenureYears = (day - npc.JobStartDay) / 365.0;
                    var promoteAnnual = 0.08 * npc.JobPerformance * Math.Min(1.0, tenureYears / 2.0);
                    if (rng.Chance(promoteAnnual / 365.0))
                    {
                        var next = occ != null ? ctx.Occupations.Get(occ.PromotesTo) : null;
                        if (next != null && npc.Education >= next.MinEducation)
                        {
                            npc.OccupationId = next.Id;
                            npc.AnnualSalaryCents = Math.Max(npc.AnnualSalaryCents, (long)(next.MinSalaryCents * (1.0 + npc.JobPerformance * 0.3)));
                            Record(npc, day, "promoted", npc.FullName + " was promoted to " + next.Title + ".", ctx);
                        }
                        else
                        {
                            npc.AnnualSalaryCents = (long)(npc.AnnualSalaryCents * (1.05 + rng.NextDouble() * 0.08));
                            Record(npc, day, "raise", npc.FullName + " got a raise.", ctx);
                        }
                        npc.JobStartDay = day;
                    }
                }
            }
            else if (npc.Employment == EmploymentStatus.Unemployed && age >= 16 && age < 67)
            {
                var findAnnual = 3.2 * Math.Max(0.2, 1.0 - unemploymentPressure * 0.5); // median search ≈ 11 weeks
                if (rng.Chance(findAnnual / 365.0)) TryHire(npc, day, rng, ctx);
            }

            // --- Finances (aggregate; NPC households are not individual ledger accounts).
            var income = DailyIncome(npc, ctx);
            var expenses = DailyLivingCost(npc, day, ctx);
            var net = income - expenses;
            if (net >= 0)
            {
                var repay = Math.Min(npc.DebtCents, net / 2);
                npc.DebtCents -= repay;
                npc.SavingsCents += net - repay;
            }
            else
            {
                var fromSavings = Math.Min(npc.SavingsCents, -net);
                npc.SavingsCents -= fromSavings;
                npc.DebtCents += -net - fromSavings;
            }
            if (npc.DebtCents > 0) npc.DebtCents += (long)(npc.DebtCents * 0.18 / 365.0);

            // Financial stress nudges criminal propensity (never deterministic criminality).
            var stressed = npc.DebtCents > npc.AnnualSalaryCents / 4 + 200000;
            npc.CriminalPropensity = Clamp01(npc.CriminalPropensity + (stressed ? 0.0004f : -0.0002f));
        }

        public static long DailyIncome(NpcRecord npc, LifeSimContext ctx)
        {
            double gross;
            switch (npc.Employment)
            {
                case EmploymentStatus.Employed: gross = npc.AnnualSalaryCents * ctx.WageMultiplier / 365.0; break;
                case EmploymentStatus.Retired: gross = Math.Max(1200000, npc.AnnualSalaryCents * 0.4) / 365.0; break;
                case EmploymentStatus.Unemployed: gross = 1400000 / 365.0; break; // benefits / odd jobs
                default: return 0;
            }
            return (long)(gross * (1.0 - ctx.IncomeTaxRate));
        }

        public static long DailyLivingCost(NpcRecord npc, long day, LifeSimContext ctx)
        {
            if (npc.Employment == EmploymentStatus.Child || npc.Employment == EmploymentStatus.Student) return 0;
            var district = ctx.Geography?.GetDistrict(DistrictOf(npc, ctx));
            var wealth = district != null ? district.Wealth : 0.5f;
            var priceLevel = ctx.Macro != null ? ctx.Macro.PriceLevel : 1.0;
            // ~$38/day in the poorest areas up to ~$120/day in the wealthiest, before dependants.
            var baseCents = 3800 + 8200 * wealth;
            return (long)(baseCents * priceLevel * ctx.CostOfLivingMultiplier);
        }

        private static EntityId DistrictOf(NpcRecord npc, LifeSimContext ctx)
        {
            var home = ctx.Geography?.GetPlace(npc.Home);
            return home != null ? home.District : EntityId.None;
        }

        private static void TryHire(NpcRecord npc, long day, DeterministicRandom rng, LifeSimContext ctx)
        {
            var candidates = new List<OccupationDefinition>();
            var weights = new List<double>();
            foreach (var occ in ctx.Occupations.All)
            {
                if (npc.Education < occ.MinEducation) continue;
                var hasWorkplace = false;
                foreach (var k in occ.WorkplaceKinds)
                    if (ctx.WorkplacesByKind.TryGetValue(k, out var l) && l.Count > 0) { hasWorkplace = true; break; }
                if (!hasWorkplace) continue;
                candidates.Add(occ);
                weights.Add(occ.DemandWeight);
            }
            var index = rng.PickWeighted(weights);
            if (index < 0) return;
            var chosen = candidates[index];
            var kinds = new List<Place>();
            foreach (var k in chosen.WorkplaceKinds)
                if (ctx.WorkplacesByKind.TryGetValue(k, out var l)) kinds.AddRange(l);
            var workplace = kinds[rng.NextInt(0, kinds.Count)];
            npc.Employment = EmploymentStatus.Employed;
            npc.OccupationId = chosen.Id;
            npc.Workplace = workplace.Id;
            npc.JobStartDay = day;
            npc.JobPerformance = 0.5f;
            npc.AnnualSalaryCents = chosen.MinSalaryCents + (long)((chosen.MaxSalaryCents - chosen.MinSalaryCents) * rng.NextDouble() * 0.4);
            Record(npc, day, "hired", npc.FullName + " started work as " + chosen.Title + " at " + workplace.Name + ".", ctx);
        }

        private static void Record(NpcRecord npc, long day, string kind, string summary, LifeSimContext ctx)
        {
            npc.AddHistory(day, kind, summary);
            ctx.Notable?.Invoke(npc, npc.History[npc.History.Count - 1]);
        }

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
