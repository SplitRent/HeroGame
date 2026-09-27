using System;
using System.Collections.Generic;
using HeroGame.Core.Characters;
using HeroGame.Core.Civic;
using HeroGame.Core.Economy;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.World;
using PhoneCategory = HeroGame.Core.Phone.MessageCategory;

namespace HeroGame.Core.Simulation
{
    /// <summary>
    /// City government (GDD §24–27): a municipal budget whose department funding sets real service levels (police,
    /// fire and ambulance units on duty, flood defences, parks), ordinances with typed effects voted by an elected
    /// council, public opinion that moves with what actually happens in each district, and nonpartisan elections in
    /// which every adult NPC may vote — alongside players, who can also run for office.
    /// </summary>
    public sealed class CivicService
    {
        /// <summary>Monthly per-resident operating need (cents) — ~$2,000 per resident per year, typical of a large US city.</summary>
        public const long NeedPerResidentMonthCents = 16600;
        /// <summary>Monthly per-resident local taxes collected from households (property and sales) the aggregate NPC economy does not book.</summary>
        public const long RevenuePerResidentMonthCents = 15000;
        public const long FilingFeeCents = 50000;
        public const long DonationCapCents = 500000;
        public const int CampaignDays = 30;

        private readonly World _w;
        private readonly Dictionary<string, OrdinanceDefinition> _ordinances = new Dictionary<string, OrdinanceDefinition>();
        private readonly float _basePropertyTax;
        private int _lastCurfew = -1;
        private readonly float _baseSalesTax;

        public CivicService(World world)
        {
            _w = world;
            _basePropertyTax = world.Config.Economy.PropertyTaxAnnualRate;
            _baseSalesTax = world.Config.Economy.SalesTaxRate;
            foreach (var o in world.Content.Ordinances) _ordinances[o.Id] = o;
            world.History.Recorded += OnHistory;
        }

        private CivicState S => _w.Civic;

        public OrdinanceDefinition Ordinance(string id) => id != null && _ordinances.TryGetValue(id, out var o) ? o : null;
        public IEnumerable<OrdinanceDefinition> Ordinances => _ordinances.Values;

        // ------------------------------------------------------------------ setup

        /// <summary>Default budget, initial opinion and a sitting mayor and council (idempotent).</summary>
        public void EnsureInitialized()
        {
            if (S.Budget.Departments.Count == 0)
            {
                void Dept(Department d, float share) => S.Budget.Departments.Add(new DepartmentBudget { Department = d, Share = share });
                Dept(Department.Police, 0.30f);
                Dept(Department.Fire, 0.14f);
                Dept(Department.Health, 0.12f);
                Dept(Department.PublicWorks, 0.24f);
                Dept(Department.Parks, 0.08f);
                Dept(Department.Housing, 0.12f);
            }
            foreach (var d in SortedDistricts())
            {
                var o = S.OpinionOf(d.Id);
                foreach (var issue in Issues.All)
                    if (!o.Support.ContainsKey(issue)) o.Support[issue] = Baseline(d, issue);
            }
            if (S.Officeholders.Count == 0)
            {
                var districts = SortedDistricts();
                for (var i = 0; i < districts.Count; i++)
                {
                    var d = districts[i];
                    // Wards with few residents (docks, industrial estates) are represented by someone from elsewhere.
                    var npc = Notable(d.Id, 0);
                    if (npc == null || S.Officeholders.Exists(o => o.Person == npc.Id)) npc = Notable(EntityId.None, 10 + i);
                    if (npc != null)
                        S.Officeholders.Add(new Officeholder { Office = Office.Council, District = d.Key, Person = npc.Id, Name = npc.FullName, Slate = d.Wealth >= 0.55f ? Slates.Renewal : Slates.Working, TermEndsDay = long.MaxValue });
                }
                var mayor = Notable(districts.Count > 0 ? districts[0].Id : EntityId.None, 1);
                if (mayor != null) S.Officeholders.Add(new Officeholder { Office = Office.Mayor, Person = mayor.Id, Name = mayor.FullName, Slate = Slates.Renewal, TermEndsDay = long.MaxValue });
            }
        }

        /// <summary>After generation or load: defaults, then re-derive everything the ordinances and budget control.</summary>
        public void OnLoaded()
        {
            EnsureInitialized();
            ApplyOrdinanceEffects();
            ApplyServiceLevels();
        }

        private List<District> SortedDistricts()
        {
            var list = new List<District>(_w.Geography.Districts);
            list.Sort((a, b) => a.Id.CompareTo(b.Id));
            return list;
        }

        /// <summary>Where a district starts on each issue, from its own circumstances.</summary>
        private static float Baseline(District d, string issue)
        {
            switch (issue)
            {
                case Issues.FloodControl: return Clamp(d.FloodRisk * 1.4f - 0.1f);
                case Issues.Development: return Clamp((d.Wealth - 0.5f) * 1.2f);
                case Issues.Policing: return Clamp(d.CrimeBaseline * 1.5f - 0.2f);
                case Issues.Taxes: return Clamp(-0.2f - d.Wealth * 0.3f);
                default: return 0f;
            }
        }

        private NpcRecord Notable(EntityId district, int skip)
        {
            var list = new List<NpcRecord>();
            foreach (var npc in _w.Population.Ordered)
            {
                if (!npc.Alive || npc.Education < EducationLevel.Bachelors || npc.AgeYears(_w.Today) < 35) continue;
                var home = _w.Geography.GetPlace(npc.Home);
                if (district.IsValid && (home == null || home.District != district)) continue;
                list.Add(npc);
            }
            return list.Count > skip ? list[skip] : list.Count > 0 ? list[0] : null;
        }

        // ------------------------------------------------------------------ daily

        public void ProcessDay(long day)
        {
            EnsureInitialized();
            DriftOpinion();
            if (day % 30 == 0) MonthlyBudget(day);
            DecideProposals(day);
            if (day % 30 == 15) CouncilAgenda(day);
            Elections(day);
            _w.Dirty.Mark(SaveChunks.Civic);
        }

        private void DriftOpinion()
        {
            foreach (var d in SortedDistricts())
            {
                var o = S.OpinionOf(d.Id);
                foreach (var issue in Issues.All)
                    o.Support[issue] = Clamp(o.Of(issue) + (Baseline(d, issue) - o.Of(issue)) * 0.01f);
            }
        }

        /// <summary>Events move opinion where they happen: fear after anomalies, flood control after storms…</summary>
        private void OnHistory(HistoryRecord r)
        {
            var targets = new List<DistrictOpinion>();
            foreach (var d in SortedDistricts()) if (!r.District.IsValid || r.District == d.Id) targets.Add(S.OpinionOf(d.Id));
            var weight = r.District.IsValid ? 1f : 0.5f;
            foreach (var o in targets)
            {
                switch (r.Category)
                {
                    case HistoryCategory.Anomaly:
                        o.Support[Issues.Registration] = Clamp(o.Of(Issues.Registration) + 0.05f * weight * r.Importance / 3f);
                        break;
                    case HistoryCategory.Crime:
                        o.Support[Issues.Policing] = Clamp(o.Of(Issues.Policing) + 0.015f * weight * r.Importance);
                        break;
                    case HistoryCategory.Weather:
                    case HistoryCategory.Disaster:
                        o.Support[Issues.FloodControl] = Clamp(o.Of(Issues.FloodControl) + 0.04f * weight * r.Importance / 3f);
                        break;
                    case HistoryCategory.Economy:
                        if (r.Headline.IndexOf("Meridian", StringComparison.Ordinal) >= 0 || r.Headline.IndexOf("repossess", StringComparison.OrdinalIgnoreCase) >= 0)
                            o.Support[Issues.Development] = Clamp(o.Of(Issues.Development) - 0.04f * weight);
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ budget

        public int Residents()
        {
            var n = 0;
            foreach (var npc in _w.Population.Ordered) if (npc.Alive) n++;
            return n + _w.Characters.Count;
        }

        private void MonthlyBudget(long day)
        {
            if (S.Budget.LastProcessedDay == day) return;
            S.Budget.LastProcessedDay = day;
            var residents = Residents();
            var when = _w.Clock.Now;

            // Household taxes the aggregate NPC economy owes the city.
            var revenue = residents * RevenuePerResidentMonthCents;
            _w.Transactions.Execute(new WorldTransaction
            {
                Source = TransactionSource.Simulation, Timestamp = when, Description = "Household taxes",
                Money = LedgerTransaction.Transfer(_w.Accounts.External, _w.Accounts.Treasury, new Money(revenue), TransactionReason.Tax, "Property and sales taxes"),
            });
            S.Budget.LastMonthRevenueCents = revenue;

            long spent = 0;
            foreach (var dept in S.Budget.Departments)
            {
                var need = (long)(residents * NeedPerResidentMonthCents * DefaultShare(dept.Department));
                var allocation = (long)(residents * NeedPerResidentMonthCents * S.Budget.SpendingRate / 0.85f * dept.Share * (1.0 + Bonus(dept.Department) / 100.0));
                var paid = _w.Transactions.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation, Timestamp = when, Description = dept.Department + " budget",
                    Money = LedgerTransaction.Transfer(_w.Accounts.Treasury, _w.Accounts.External, new Money(Math.Max(1, allocation)), TransactionReason.Payroll, dept.Department + " operations"),
                }).Success;
                var funded = paid ? allocation : 0;
                spent += funded;
                dept.SpentThisYearCents += funded;
                dept.ServiceLevel = Math.Max(0.3f, Math.Min(1.5f, need <= 0 ? 1f : funded / (float)need));
            }
            S.Budget.LastMonthSpendingCents = spent;
            ApplyServiceLevels();
            DistrictDrift();
        }

        private static float DefaultShare(Department d)
        {
            switch (d)
            {
                case Department.Police: return 0.30f;
                case Department.Fire: return 0.14f;
                case Department.Health: return 0.12f;
                case Department.PublicWorks: return 0.24f;
                case Department.Parks: return 0.08f;
                default: return 0.12f;
            }
        }

        private double Bonus(Department d)
        {
            var key = d == Department.Police ? "PoliceBonusPercent" : d == Department.Fire ? "FireBonusPercent" : d == Department.PublicWorks ? "PublicWorksBonusPercent" : null;
            return key == null ? 0 : Effect(key);
        }

        /// <summary>Sum of an effect across active ordinances.</summary>
        public double Effect(string key)
        {
            double total = 0;
            foreach (var a in S.Ordinances)
                if (_ordinances.TryGetValue(a.Id, out var def) && def.Effects.TryGetValue(key, out var v)) total += v;
            return total;
        }

        public bool Has(string key)
        {
            foreach (var a in S.Ordinances)
                if (_ordinances.TryGetValue(a.Id, out var def) && def.Effects.ContainsKey(key)) return true;
            return false;
        }

        public float ServiceLevel(Department d) => S.Budget.Of(d)?.ServiceLevel ?? 1f;

        /// <summary>Funding becomes capacity: units on duty, response speed, flood defences, trust, amenity.</summary>
        public void ApplyServiceLevels()
        {
            SetOnDuty(EmergencyService.Police, ServiceLevel(Department.Police));
            SetOnDuty(EmergencyService.Fire, ServiceLevel(Department.Fire));
            SetOnDuty(EmergencyService.Medical, ServiceLevel(Department.Health));
            var response = Has("PoliceResponseMultiplier") ? Effect("PoliceResponseMultiplier") : 1.0;
            _w.Wanted.Settings.ResponseMultiplier = (float)(_w.Config.Gameplay.PoliceResponseMultiplier * response * (0.7 + 0.3 * Math.Min(1.3f, ServiceLevel(Department.Police))));
            _w.Dirty.Mark(SaveChunks.Emergency);
        }

        /// <summary>Monthly consequences of funding for each district: flood defences, park amenity, trust in police.</summary>
        private void DistrictDrift()
        {
            var works = ServiceLevel(Department.PublicWorks);
            var parks = ServiceLevel(Department.Parks);
            var police = ServiceLevel(Department.Police);
            foreach (var d in SortedDistricts())
            {
                d.FloodRisk = Clamp01(d.FloodRisk + (1f - works) * 0.004f - (float)(Effect("FloodRiskReductionPerYear") / 12.0) * Math.Min(1f, works));
                d.FloodRisk = Math.Max(0.05f, d.FloodRisk);
                d.Wealth = Clamp01(d.Wealth + (parks - 1f) * 0.002f);
                d.PoliceTrust = Clamp01(d.PoliceTrust + (police - 1f) * 0.01f + (float)Effect("PoliceTrustDriftPerMonth"));
                // A curfew is enforced hardest where trust is lowest: it costs goodwill in poorer districts.
                if (Has("CurfewStartHour") && d.Wealth < 0.4f) d.PoliceTrust = Clamp01(d.PoliceTrust - 0.005f);
            }
            _w.Dirty.Mark(SaveChunks.Environment);
        }

        private void SetOnDuty(EmergencyService service, float level)
        {
            var units = _w.Emergency.Units.FindAll(u => u.Service == service);
            units.Sort((a, b) => a.Vehicle.CompareTo(b.Vehicle));
            var onDuty = (int)Math.Ceiling(units.Count * Math.Max(0.34f, Math.Min(1f, level)));
            for (var i = 0; i < units.Count; i++) units[i].OffDuty = i >= onDuty;
        }

        /// <summary>The mayor's budget: department shares (normalised) and the overall spending rate.</summary>
        public OpResult SetBudget(ServerCharacter mayor, Dictionary<Department, float> shares, float spendingRate)
        {
            if (!Holds(mayor.CharacterId, Office.Mayor)) return OpResult.Fail("Only the mayor sets the budget.");
            var total = 0f;
            foreach (var kv in shares) total += Math.Max(0f, kv.Value);
            if (total <= 0f) return OpResult.Fail("The budget must fund something.");
            foreach (var dept in S.Budget.Departments)
                if (shares.TryGetValue(dept.Department, out var s)) dept.Share = Math.Max(0f, s) / total;
            S.Budget.SpendingRate = Math.Max(0.5f, Math.Min(1.2f, spendingRate));
            _w.History.Record(_w.Today, HistoryCategory.Politics, 3, "Mayor " + mayor.CharacterId + " signs the city budget");
            return OpResult.Ok();
        }

        // ------------------------------------------------------------------ ordinances

        public bool Holds(EntityId person, Office office) => S.Officeholders.Exists(o => o.Person == person && o.Office == office);
        public bool HoldsOffice(EntityId person) => S.Officeholders.Exists(o => o.Person == person);

        public OpResult Propose(EntityId proposer, string ordinanceId, bool repeal)
        {
            var def = Ordinance(ordinanceId);
            if (def == null) return OpResult.Fail("Unknown ordinance.");
            if (proposer.IsValid && !HoldsOffice(proposer)) return OpResult.Fail("Only the mayor or a council member can bring an ordinance to a vote.");
            if (repeal != S.IsActive(ordinanceId)) return OpResult.Fail(repeal ? "That ordinance is not in force." : "That ordinance is already in force.");
            if (S.Proposals.Exists(p => !p.Decided && p.OrdinanceId == ordinanceId)) return OpResult.Fail("Already on the agenda.");
            S.Proposals.Add(new Proposal { OrdinanceId = ordinanceId, ProposedBy = proposer, VoteDay = _w.Today + 7, Repeal = repeal ? "repeal" : "" });
            _w.History.Record(_w.Today, HistoryCategory.Politics, 3, "Council to vote on " + (repeal ? "repealing the " : "the ") + def.Title, def.Description);
            return OpResult.Ok();
        }

        /// <summary>NPC-led agenda: once a month the council takes up the issue the public feels most strongly about.</summary>
        private void CouncilAgenda(long day)
        {
            if (S.Proposals.Exists(p => !p.Decided)) return;
            string bestIssue = null;
            var bestMagnitude = 0.25f;
            foreach (var issue in Issues.All)
            {
                var avg = 0f;
                var n = 0;
                foreach (var o in S.Opinion) { avg += o.Of(issue); n++; }
                avg = n > 0 ? avg / n : 0f;
                if (Math.Abs(avg) > bestMagnitude) { bestMagnitude = Math.Abs(avg); bestIssue = issue; }
            }
            if (bestIssue == null) return;
            var sign = 0f;
            foreach (var o in S.Opinion) sign += o.Of(bestIssue);
            var candidates = new List<OrdinanceDefinition>();
            foreach (var def in _ordinances.Values) if (def.Issue == bestIssue) candidates.Add(def);
            candidates.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            foreach (var def in candidates)
            {
                var wanted = sign * def.Stance > 0;
                if (wanted && !S.IsActive(def.Id)) { Propose(EntityId.None, def.Id, repeal: false); return; }
                if (!wanted && S.IsActive(def.Id)) { Propose(EntityId.None, def.Id, repeal: true); return; }
            }
        }

        /// <summary>A player officeholder casts their council vote before the vote day.</summary>
        public OpResult CastCouncilVote(ServerCharacter member, string ordinanceId, bool aye)
        {
            if (!HoldsOffice(member.CharacterId)) return OpResult.Fail("You don't hold office.");
            var p = S.Proposals.Find(x => !x.Decided && x.OrdinanceId == ordinanceId);
            if (p == null) return OpResult.Fail("Nothing to vote on.");
            p.PlayerVotes[member.CharacterId.ToString()] = aye;
            _w.Dirty.Mark(SaveChunks.Civic);
            return OpResult.Ok();
        }

        private void DecideProposals(long day)
        {
            foreach (var p in S.Proposals)
            {
                if (p.Decided || p.VoteDay > day) continue;
                var def = Ordinance(p.OrdinanceId);
                if (def == null)
                {
                    p.Decided = true; // withdrawn from the content set since it was proposed
                    continue;
                }
                var yes = p.Repeal == "" ? 1 : -1;
                foreach (var member in S.Officeholders)
                {
                    bool aye;
                    if (member.IsPlayer && p.PlayerVotes.TryGetValue(member.Person.ToString(), out var v)) aye = v;
                    else if (member.IsPlayer) aye = false; // absent
                    else
                    {
                        var district = DistrictByKey(member.District);
                        var opinion = district != null ? S.OpinionOf(district.Id).Of(def.Issue) : AverageOpinion(def.Issue);
                        var lean = Slates.Stance(member.Slate, def.Issue) * def.Stance * yes * 0.6f + opinion * def.Stance * yes * 0.8f;
                        var rng = DeterministicRandom.For(_w.Seed, member.Person.Value, StableHash.Of(p.OrdinanceId), (ulong)day);
                        aye = rng.NextDouble() < 1.0 / (1.0 + Math.Exp(-3.0 * lean));
                    }
                    if (aye) p.Ayes++;
                    else p.Nays++;
                }
                p.Decided = true;
                p.Passed = p.Ayes > p.Nays;
                if (p.Passed)
                {
                    if (p.Repeal == "") Enact(def, p.ProposedBy);
                    else Repeal(def);
                }
                _w.History.Record(day, HistoryCategory.Politics, 4,
                    "Council " + (p.Passed ? "passes " : "rejects ") + (p.Repeal == "" ? "" : "repeal of ") + def.Title + " " + p.Ayes + "–" + p.Nays);
            }
            S.Proposals.RemoveAll(p => p.Decided && day - p.VoteDay > 60);
        }

        private float AverageOpinion(string issue)
        {
            var sum = 0f;
            foreach (var o in S.Opinion) sum += o.Of(issue);
            return S.Opinion.Count > 0 ? sum / S.Opinion.Count : 0f;
        }

        private District DistrictByKey(string key)
        {
            foreach (var d in _w.Geography.Districts) if (d.Key == key) return d;
            return null;
        }

        /// <summary>Puts an ordinance in force and applies its effects (tax rates, permit fees, foot traffic, rent cap).</summary>
        public void Enact(OrdinanceDefinition def, EntityId proposer = default)
        {
            if (def == null || S.IsActive(def.Id)) return;
            S.Ordinances.Add(new ActiveOrdinance { Id = def.Id, EnactedDay = _w.Today, ProposedBy = proposer });
            ApplyOneTime(def, +1);
            ApplyOrdinanceEffects();
            ApplyServiceLevels();
            _w.Dirty.Mark(SaveChunks.Civic);
        }

        public void Repeal(OrdinanceDefinition def)
        {
            if (def == null || !S.IsActive(def.Id)) return;
            S.Ordinances.RemoveAll(o => o.Id == def.Id);
            ApplyOneTime(def, -1);
            ApplyOrdinanceEffects();
            ApplyServiceLevels();
            _w.Dirty.Mark(SaveChunks.Civic);
        }

        /// <summary>
        /// Recomputes the continuous effects of the ordinances in force from the server's configured baseline, so they
        /// survive restarts without being persisted in the config (called after load and after every enactment/repeal).
        /// </summary>
        public void ApplyOrdinanceEffects()
        {
            var econ = _w.Config.Economy;
            econ.PropertyTaxAnnualRate = Has("PropertyTaxRate") ? (float)Effect("PropertyTaxRate") : _basePropertyTax;
            econ.SalesTaxRate = Has("SalesTaxRate") ? (float)Effect("SalesTaxRate") : _baseSalesTax;
            _w.Construction.PermitFeeMultiplier = (float)Math.Max(0.1, 1.0 + Effect("PermitFeePercent") / 100.0);
            // Youth curfew: minors' schedules keep them home in the window.
            var curfew = Has("CurfewStartHour");
            _w.Schedules.CurfewStartMinute = curfew ? (int)Effect("CurfewStartHour") % 24 * 60 : -1;
            _w.Schedules.CurfewEndMinute = curfew ? (int)Effect("CurfewEndHour") % 24 * 60 : -1;
            if (_w.Schedules.CurfewStartMinute != _lastCurfew)
            {
                _lastCurfew = _w.Schedules.CurfewStartMinute;
                _w.Director.Index.Clear(); // cached whereabouts were computed under the old rules
            }
            _w.Rentals.MaxAnnualIncreasePercent = (float)Effect("RentIncreaseCapPercent");
        }

        /// <summary>Effects that change persisted world state once (district foot traffic).</summary>
        private void ApplyOneTime(OrdinanceDefinition def, int direction)
        {
            if (def.Effects.TryGetValue("ChannelsideFootTrafficBonus", out var bonus))
            {
                var d = DistrictByKey("channelside");
                if (d != null)
                {
                    d.FootTraffic = Math.Max(0.1f, d.FootTraffic + (float)bonus * direction);
                    _w.Dirty.Mark(SaveChunks.Environment);
                }
            }
        }

        /// <summary>Under a youth curfew, minors may not be out in public between the configured hours.</summary>
        public bool CurfewAt(int hour)
        {
            if (!Has("CurfewStartHour")) return false;
            var start = (int)Effect("CurfewStartHour");
            var end = (int)Effect("CurfewEndHour");
            return start > end ? hour >= start || hour < end : hour >= start && hour < end;
        }

        // ------------------------------------------------------------------ anomalous abilities registration

        public bool RegistrationRequired => Has("RegistrationRequired");
        public bool IsRegistered(EntityId character) => S.RegisteredPowered.Contains(character);

        public OpResult RegisterPowers(ServerCharacter c)
        {
            if (!c.Powers.IsPowered) return OpResult.Fail("You have nothing to register.");
            if (IsRegistered(c.CharacterId)) return OpResult.Fail("Already registered.");
            S.RegisteredPowered.Add(c.CharacterId);
            _w.Dirty.Mark(SaveChunks.Civic);
            _w.Phone.Send(c, _w.Accounts.Government, _w.Config.Identity.GovernmentName, PhoneCategory.Server,
                "Your registration is on file. Registered persons may use abilities in public under city rules.");
            return OpResult.Ok();
        }

        // ------------------------------------------------------------------ elections

        private void Elections(long day)
        {
            var interval = Math.Max(30, _w.Config.Gameplay.ElectionIntervalDays);
            if (S.Elections.Count == 0 && (S.LastElectionScheduledDay < 0 || day - S.LastElectionScheduledDay >= interval))
                ScheduleElections(day);
            foreach (var e in S.Elections)
            {
                if (e.Held) continue;
                Campaign(e, day);
                if (day >= e.ElectionDay) Hold(e, day);
            }
            foreach (var e in S.Elections) if (e.Held) S.PastElections.Add(e);
            S.Elections.RemoveAll(e => e.Held);
            if (S.PastElections.Count > 20) S.PastElections.RemoveRange(0, S.PastElections.Count - 20);
        }

        public void ScheduleElections(long day)
        {
            S.LastElectionScheduledDay = day;
            var mayor = NewElection(Office.Mayor, "", day);
            foreach (var d in SortedDistricts()) NewElection(Office.Council, d.Key, day);
            _w.History.Record(day, HistoryCategory.Politics, 4, "Filing opens for mayor and city council; election on day " + mayor.ElectionDay);
            foreach (var c in _w.Characters.Values)
                _w.Phone.Send(c, _w.Accounts.Government, _w.Config.Identity.GovernmentName, PhoneCategory.Server,
                    "City elections in " + CampaignDays + " days. File as a candidate at City Hall" + (_w.Config.Gameplay.PlayerElections ? "" : " (player candidacies are disabled on this server)") + ", and remember to vote.");
        }

        private Election NewElection(Office office, string district, long day)
        {
            var e = new Election { Id = office + ":" + district + ":" + day, Office = office, District = district, FilingOpensDay = day, ElectionDay = day + CampaignDays };
            var incumbent = S.Officeholders.Find(o => o.Office == office && o.District == district);
            if (incumbent != null)
                e.Candidates.Add(new Candidate { Person = incumbent.Person, IsPlayer = incumbent.IsPlayer, Name = incumbent.Name, Slate = incumbent.Slate, District = district, Incumbent = true, Recognition = 0.5f });
            // The other slate always fields a challenger; sometimes an independent runs too.
            var d = DistrictByKey(district);
            var challengerSlate = incumbent != null && incumbent.Slate == Slates.Renewal ? Slates.Working : Slates.Renewal;
            var npc = Notable(d != null ? d.Id : EntityId.None, 2 + (int)(day % 5));
            if (npc != null && (incumbent == null || npc.Id != incumbent.Person))
                e.Candidates.Add(new Candidate { Person = npc.Id, Name = npc.FullName, Slate = challengerSlate, District = district, Recognition = 0.2f });
            var rng = DeterministicRandom.For(_w.Seed, StableHash.Of(e.Id), 0x1D);
            if (rng.Chance(0.35))
            {
                var ind = Notable(d != null ? d.Id : EntityId.None, 7 + (int)(day % 3));
                if (ind != null && !e.Candidates.Exists(c => c.Person == ind.Id))
                    e.Candidates.Add(new Candidate { Person = ind.Id, Name = ind.FullName, Slate = Slates.Independent, District = district, Recognition = 0.1f });
            }
            S.Elections.Add(e);
            return e;
        }

        /// <summary>A player files as a candidate: fee to the city, a new campaign account, on the ballot.</summary>
        public OpResult FileCandidacy(ServerCharacter c, Office office, string district, string slate, string displayName, string idempotencyKey)
        {
            if (!_w.Config.Gameplay.PlayerElections) return OpResult.Fail("Player candidacies are disabled on this server.");
            var e = S.Elections.Find(x => x.Office == office && x.District == (office == Office.Mayor ? "" : district) && !x.Held);
            if (e == null) return OpResult.Fail("No election is open for that office.");
            if (_w.Today > e.ElectionDay - 5) return OpResult.Fail("Filing has closed.");
            if (e.Candidates.Exists(x => x.Person == c.CharacterId)) return OpResult.Fail("You are already on the ballot.");
            if (c.Record.InCustody || c.Record.ActiveWarrant) return OpResult.Fail("Candidates must be free and have no outstanding warrants.");
            if (c.Record.Convictions > 0 && c.Record.Charges.Exists(ch => ch.Convicted && (_w.Content.FindCrime(ch.CrimeTypeId)?.Severity ?? 0) >= 6))
                return OpResult.Fail("Serious convictions disqualify candidates.");
            if (slate != Slates.Renewal && slate != Slates.Working) slate = Slates.Independent;
            // A returning candidate keeps their campaign account (and what is left in it); a first-timer opens one.
            var reuse = S.CampaignAccounts.TryGetValue(c.CharacterId.ToString(), out var account) && _w.Ledger.Exists(account);
            if (!reuse) account = _w.Ids.Next(EntityKind.LedgerAccount);
            var tx = new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Filing fee",
                Money = LedgerTransaction.Transfer(c.CheckingAccount, _w.Accounts.Treasury, new Money(FilingFeeCents), TransactionReason.Fee, "Candidate filing fee"),
            };
            if (!reuse) tx.OpenAccounts.Add(new LedgerAccount { Id = account, Owner = c.CharacterId, Kind = LedgerAccountKind.Organization, Label = displayName + " campaign" });
            var result = _w.Transactions.Execute(tx);
            if (!result.Success) return result;
            S.CampaignAccounts[c.CharacterId.ToString()] = account;
            e.Candidates.Add(new Candidate { Person = c.CharacterId, IsPlayer = true, Name = displayName, Slate = slate, District = e.District, CampaignAccount = account, Recognition = 0.05f });
            _w.History.Record(_w.Today, HistoryCategory.Politics, 3, displayName + " enters the race for " + (office == Office.Mayor ? "mayor" : district + " council"));
            return result;
        }

        public OpResult Donate(ServerCharacter donor, string electionId, EntityId candidate, Money amount, string idempotencyKey)
        {
            var e = S.Elections.Find(x => x.Id == electionId && !x.Held);
            var cand = e?.Candidates.Find(x => x.Person == candidate);
            if (cand == null || !cand.CampaignAccount.IsValid) return OpResult.Fail("That campaign does not take donations.");
            if (amount.Cents <= 0 || amount.Cents > DonationCapCents) return OpResult.Fail("Donations are limited to " + new Money(DonationCapCents) + ".");
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = donor.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Campaign donation",
                Money = LedgerTransaction.Transfer(donor.CheckingAccount, cand.CampaignAccount, amount, TransactionReason.PlayerTransfer, "Donation to " + cand.Name),
            });
            if (result.Success) cand.RaisedCents += amount.Cents;
            return result;
        }

        /// <summary>Campaign advertising: spending buys recognition with diminishing returns.</summary>
        public OpResult SpendCampaign(ServerCharacter c, string electionId, Money amount, string idempotencyKey)
        {
            var e = S.Elections.Find(x => x.Id == electionId && !x.Held);
            var cand = e?.Candidates.Find(x => x.Person == c.CharacterId);
            if (cand == null) return OpResult.Fail("You are not a candidate.");
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Campaign ads",
                Money = LedgerTransaction.Transfer(cand.CampaignAccount, _w.Accounts.External, amount, TransactionReason.Purchase, "Campaign advertising"),
            });
            if (!result.Success) return result;
            cand.SpentCents += amount.Cents;
            cand.Recognition = Math.Min(0.95f, cand.Recognition + (float)Math.Sqrt(amount.Cents / 100.0) / 400f);
            return result;
        }

        public OpResult CastBallot(ServerCharacter voter, string electionId, EntityId candidate)
        {
            var e = S.Elections.Find(x => x.Id == electionId && !x.Held);
            if (e == null) return OpResult.Fail("That election is not open.");
            if (!e.Candidates.Exists(x => x.Person == candidate)) return OpResult.Fail("Not on the ballot.");
            if (e.PlayerVoters.Contains(voter.CharacterId)) return OpResult.Fail("You already voted.");
            if (voter.Record.InCustody) return OpResult.Fail("You can't vote from custody here.");
            e.PlayerVoters.Add(voter.CharacterId);
            e.PlayerBallots.Add(candidate.ToString());
            return OpResult.Ok();
        }

        private void Campaign(Election e, long day)
        {
            foreach (var c in e.Candidates)
                if (!c.IsPlayer) c.Recognition = Math.Min(0.8f, c.Recognition + (c.Incumbent ? 0.003f : c.Slate == Slates.Independent ? 0.004f : 0.008f));
            // Daily tracking poll from a deterministic sample of the electorate.
            var voters = Electorate(e);
            var sample = Math.Min(200, voters.Count);
            var counts = new int[e.Candidates.Count];
            var rng = DeterministicRandom.For(_w.Seed, StableHash.Of(e.Id), (ulong)day, 0x9011);
            for (var i = 0; i < sample; i++)
            {
                var v = voters[rng.NextInt(0, voters.Count)];
                var choice = Choose(e, v, rng);
                if (choice >= 0) counts[choice]++;
            }
            for (var i = 0; i < counts.Length; i++) e.Candidates[i].Polling = sample > 0 ? counts[i] / (float)sample : 0f;
        }

        private List<NpcRecord> Electorate(Election e)
        {
            var district = DistrictByKey(e.District);
            var list = new List<NpcRecord>();
            foreach (var npc in _w.Population.Ordered)
            {
                if (!npc.Alive || npc.AgeYears(_w.Today) < 18) continue;
                if (district != null)
                {
                    var home = _w.Geography.GetPlace(npc.Home);
                    if (home == null || home.District != district.Id) continue;
                }
                list.Add(npc);
            }
            return list;
        }

        /// <summary>
        /// One voter's choice: agreement between their views and each candidate's slate on the issues, how well they
        /// know the candidate, incumbency, and noise. Views start from their district and shift with their situation.
        /// </summary>
        private int Choose(Election e, NpcRecord v, DeterministicRandom rng)
        {
            var home = _w.Geography.GetPlace(v.Home);
            var opinion = home != null ? S.OpinionOf(home.District) : null;
            var household = _w.Population.GetHousehold(v.Household);
            var owner = household != null && household.OwnsHome;
            var best = -1;
            var bestU = double.MinValue;
            for (var i = 0; i < e.Candidates.Count; i++)
            {
                var c = e.Candidates[i];
                double u = 0;
                foreach (var issue in Issues.All)
                {
                    var view = opinion != null ? opinion.Of(issue) : 0f;
                    if (issue == Issues.Development) view += owner ? 0.2f : -0.2f;
                    if (issue == Issues.Registration) view -= (v.Personality.Openness - 0.5f) * 0.6f;
                    if (issue == Issues.Policing && v.Employment == EmploymentStatus.Unemployed) view -= 0.15f;
                    u += view * Slates.Stance(c.Slate, issue) * 0.6;
                }
                u += c.Recognition * 0.8 + (c.Incumbent ? 0.15 : 0) + rng.NextGaussian() * 0.3;
                if (u > bestU) { bestU = u; best = i; }
            }
            return best;
        }

        private void Hold(Election e, long day)
        {
            var voters = Electorate(e);
            e.EligibleVoters = voters.Count + e.PlayerVoters.Count;
            var bad = _w.Weather.Effects.BadWeather;
            foreach (var v in voters)
            {
                var rng = DeterministicRandom.For(_w.Seed, v.Id.Value, StableHash.Of(e.Id), 0xBA11);
                var age = v.AgeYears(_w.Today);
                var turnout = 0.22 + (age > 45 ? 0.25 : age > 30 ? 0.12 : 0) + v.Personality.Conscientiousness * 0.15 - bad * 0.2;
                if (!rng.Chance(turnout)) continue;
                var choice = Choose(e, v, rng);
                if (choice < 0) continue;
                e.Candidates[choice].Votes++;
                e.Turnout++;
            }
            foreach (var ballot in e.PlayerBallots)
            {
                var c = e.Candidates.Find(x => x.Person.ToString() == ballot);
                if (c == null) continue;
                c.Votes++;
                e.Turnout++;
            }
            e.Held = true;
            Candidate winner = null;
            foreach (var c in e.Candidates)
                if (winner == null || c.Votes > winner.Votes || c.Votes == winner.Votes && c.Person.CompareTo(winner.Person) < 0) winner = c;
            if (winner == null) return;
            e.Winner = winner.Person;
            S.Officeholders.RemoveAll(o => o.Office == e.Office && o.District == e.District);
            S.Officeholders.Add(new Officeholder
            {
                Office = e.Office, District = e.District, Person = winner.Person, Name = winner.Name, Slate = winner.Slate, IsPlayer = winner.IsPlayer,
                TermEndsDay = day + Math.Max(30, _w.Config.Gameplay.ElectionIntervalDays),
            });
            var title = e.Office == Office.Mayor ? "mayor" : e.District + " council member";
            _w.History.Record(day, HistoryCategory.Politics, e.Office == Office.Mayor ? 5 : 4, winner.Name + " elected " + title,
                winner.Votes + " of " + e.Turnout + " votes; turnout " + (e.EligibleVoters > 0 ? (100 * e.Turnout / e.EligibleVoters) + "%" : "n/a") + ".");
            foreach (var c in _w.Characters.Values)
                if (e.PlayerVoters.Contains(c.CharacterId) || e.Candidates.Exists(x => x.Person == c.CharacterId))
                    _w.Phone.Send(c, _w.Accounts.Government, _w.Config.Identity.GovernmentName, PhoneCategory.Server, winner.Name + " wins the race for " + title + ".");
        }

        private static float Clamp(float v) => Math.Max(-1f, Math.Min(1f, v));
        private static float Clamp01(float v) => Math.Max(0f, Math.Min(1f, v));
    }
}
