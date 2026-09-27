using System.Collections.Generic;
using System.Linq;
using HeroGame.Core.Characters;
using HeroGame.Core.Civic;
using HeroGame.Core.Economy;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Property;
using HeroGame.Core.Simulation;
using HeroGame.Core.Time;
using HeroGame.Core.World;
using HeroGame.Persistence.Content;
using HeroGame.Persistence.Saves;
using NUnit.Framework;

namespace HeroGame.Tests
{
    public class CivicAndSocialTests
    {
        private World _world;
        private ServerCharacter _player;

        [SetUp]
        public void SetUp()
        {
            var config = TestContent.DefaultConfig();
            config.Gameplay.PlayerElections = true;
            _world = WorldGenerator.Create("civic", config, TestContent.Load(), new MemoryTransactionJournal());
            _player = NewPlayer(_world, 21, "Dana");
        }

        private static ServerCharacter NewPlayer(World w, long account, string name) =>
            w.CreateCharacter(new AccountProfile { AccountId = EntityId.Create(EntityKind.UserAccount, (ulong)account), Character = new CharacterIdentity { FirstName = name } }, new WorldPosition());

        private CivicState Civic => _world.Civic;

        /// <summary>Runs the city's daily civic step for a range of days without the rest of the simulation.</summary>
        private void CivicDays(long from, long to)
        {
            for (var d = from; d <= to; d++)
            {
                _world.Clock.AdvanceGame(GameDateTime.SecondsPerDay);
                _world.Government.ProcessDay(d);
            }
        }

        private void MakeMayor(ServerCharacter c)
        {
            Civic.Officeholders.RemoveAll(o => o.Office == Office.Mayor);
            Civic.Officeholders.Add(new Officeholder { Office = Office.Mayor, Person = c.CharacterId, Name = "Mayor", IsPlayer = true, Slate = Slates.Independent });
        }

        [Test]
        public void CivicContent_LoadsAndValidates()
        {
            var content = TestContent.Load();
            Assert.GreaterOrEqual(content.Ordinances.Count, 8);
            Assert.GreaterOrEqual(content.CalendarEvents.Count, 9);
            Assert.IsNotEmpty(content.RippleTemplates);
            var report = ContentLoader.Validate(content);
            Assert.IsFalse(report.HasErrors, report.ToString());

            content.Ordinances.Add(new OrdinanceDefinition { Id = "bogus", Issue = "nonsense", Effects = { ["MakeEveryoneRich"] = 1 } });
            try
            {
                var broken = ContentLoader.Validate(content).ToString();
                StringAssert.Contains("Unknown issue", broken);
                StringAssert.Contains("Unknown effect MakeEveryoneRich", broken);
            }
            finally
            {
                content.Ordinances.RemoveAll(o => o.Id == "bogus");
            }
        }

        [Test]
        public void NewCity_HasABudget_AMayor_AndACouncilSeatPerDistrict()
        {
            Assert.AreEqual(6, Civic.Budget.Departments.Count);
            Assert.AreEqual(1f, Civic.Budget.Departments.Sum(d => d.Share), 0.001f);
            Assert.AreEqual(1, Civic.Officeholders.Count(o => o.Office == Office.Mayor));
            Assert.AreEqual(_world.Geography.Districts.Count(), Civic.Officeholders.Count(o => o.Office == Office.Council));
            foreach (var d in _world.Geography.Districts)
                Assert.AreEqual(Issues.All.Length, Civic.OpinionOf(d.Id).Support.Count, "every district has a view on every issue");
            var flooded = _world.Geography.Districts.OrderByDescending(d => d.FloodRisk).First();
            var dry = _world.Geography.Districts.OrderBy(d => d.FloodRisk).First();
            Assert.Greater(Civic.OpinionOf(flooded.Id).Of(Issues.FloodControl), Civic.OpinionOf(dry.Id).Of(Issues.FloodControl));
        }

        [Test]
        public void MonthlyBudget_MovesRealMoney_AndCutsTakeUnitsOffDuty()
        {
            var treasury = _world.Ledger.BalanceOf(_world.Accounts.Treasury);
            CivicDays(_world.Today, (_world.Today / 30 + 1) * 30);
            Assert.Greater(Civic.Budget.LastMonthRevenueCents, 0);
            Assert.Greater(Civic.Budget.LastMonthSpendingCents, 0);
            Assert.AreNotEqual(treasury, _world.Ledger.BalanceOf(_world.Accounts.Treasury));
            Assert.AreEqual(1f, _world.Government.ServiceLevel(Department.Police), 0.05f, "the default budget funds departments at need");
            Assert.IsFalse(_world.Emergency.Units.Any(u => u.OffDuty));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));

            Assert.IsFalse(_world.Government.SetBudget(_player, new Dictionary<Department, float> { [Department.Police] = 1f }, 0.85f).Success, "only the mayor");
            MakeMayor(_player);
            var shares = new Dictionary<Department, float>
            {
                [Department.Police] = 0.05f, [Department.Fire] = 0.2f, [Department.Health] = 0.2f, [Department.PublicWorks] = 0.35f, [Department.Parks] = 0.1f, [Department.Housing] = 0.1f,
            };
            Assert.IsTrue(_world.Government.SetBudget(_player, shares, 0.85f).Success);
            var next = (_world.Today / 30 + 1) * 30;
            CivicDays(_world.Today, next);
            Assert.Less(_world.Government.ServiceLevel(Department.Police), 0.5f);
            var police = _world.Emergency.Units.Where(u => u.Service == EmergencyService.Police).ToList();
            Assert.IsTrue(police.Any(u => u.OffDuty), "a gutted police budget staffs fewer cars");
            Assert.IsTrue(police.Any(u => !u.OffDuty), "but never none");
            Assert.IsTrue(police.Where(u => u.OffDuty).All(u => !u.Free), "off-duty units take no calls");
        }

        [Test]
        public void Ordinances_ChangeRates_AndRepealRestoresThem()
        {
            var baseSales = _world.Config.Economy.SalesTaxRate;
            var gov = _world.Government;
            gov.Enact(gov.Ordinance("sales_tax_increase"));
            Assert.AreEqual(0.0875f, _world.Config.Economy.SalesTaxRate, 1e-6f);
            Assert.AreEqual(new Money(875), _world.Taxes.SalesTax(new Money(10000)), "tax policy reads the live rate");
            gov.ApplyOrdinanceEffects();
            Assert.AreEqual(0.0875f, _world.Config.Economy.SalesTaxRate, 1e-6f, "idempotent");
            gov.Repeal(gov.Ordinance("sales_tax_increase"));
            Assert.AreEqual(baseSales, _world.Config.Economy.SalesTaxRate, 1e-6f);

            var req = _world.Content.BusinessRequirements[0];
            Assert.AreEqual(req.PermitFeeCents, _world.Construction.PermitFee(req));
            var channelside = _world.Geography.Districts.FirstOrDefault(d => d.Key == "channelside");
            var traffic = channelside?.FootTraffic ?? 0f;
            gov.Enact(gov.Ordinance("port_expansion"));
            Assert.AreEqual((long)(req.PermitFeeCents * 0.7), _world.Construction.PermitFee(req));
            if (channelside != null) Assert.AreEqual(traffic + 0.15f, channelside.FootTraffic, 1e-4f);
            gov.Repeal(gov.Ordinance("port_expansion"));
            Assert.AreEqual(req.PermitFeeCents, _world.Construction.PermitFee(req));
            if (channelside != null) Assert.AreEqual(traffic, channelside.FootTraffic, 1e-4f);
        }

        [Test]
        public void RentStabilization_CapsRaisesForSittingTenants()
        {
            var p = _world.Properties.All.OrderBy(x => x.Id).First(x => x.Kind != PropertyKind.Land);
            Assert.IsTrue(_world.Transactions.Execute(new WorldTransaction
            {
                Source = TransactionSource.Admin, Timestamp = _world.Clock.Now, Description = "Test deed",
                Ownership = { new OwnershipChange { Asset = p.Id, From = _world.Ownership.OwnerOf(p.Id), To = _player.CharacterId } },
            }).Success);
            p.Tenancy = new Tenancy { Tenant = EntityId.Create(EntityKind.Npc, 1), MonthlyRentCents = 100000, StartDay = _world.Today - 400 };
            var gov = _world.Government;
            gov.Enact(gov.Ordinance("rent_stabilization"));
            Assert.IsFalse(_world.Rentals.ChangeRent(p, _player.CharacterId, new Money(110000), _world.Today).Success, "4 % cap");
            Assert.IsTrue(_world.Rentals.ChangeRent(p, _player.CharacterId, new Money(104000), _world.Today).Success);
            Assert.IsFalse(_world.Rentals.ChangeRent(p, _player.CharacterId, new Money(105000), _world.Today + 30).Success, "once a year");
            Assert.IsTrue(_world.Rentals.ChangeRent(p, _player.CharacterId, new Money(90000), _world.Today + 30).Success, "cuts are always allowed");
            gov.Repeal(gov.Ordinance("rent_stabilization"));
            Assert.IsTrue(_world.Rentals.ChangeRent(p, _player.CharacterId, new Money(200000), _world.Today + 31).Success, "uncapped again");
        }

        [Test]
        public void Council_VotesOnProposals_AndTheOutcomeTakesEffect()
        {
            var gov = _world.Government;
            Assert.IsFalse(gov.Propose(_player.CharacterId, "flood_control_bond", repeal: false).Success, "only officeholders");
            Assert.IsTrue(gov.Propose(EntityId.None, "flood_control_bond", repeal: false).Success);
            Assert.IsFalse(gov.Propose(EntityId.None, "flood_control_bond", repeal: false).Success, "already on the agenda");
            CivicDays(_world.Today, _world.Today + 8);
            var p = Civic.Proposals.Single(x => x.OrdinanceId == "flood_control_bond");
            Assert.IsTrue(p.Decided);
            Assert.AreEqual(Civic.Officeholders.Count, p.Ayes + p.Nays);
            Assert.AreEqual(p.Passed, Civic.IsActive("flood_control_bond"));
            Assert.IsTrue(_world.History.Recent.Any(r => r.Category == HistoryCategory.Politics && r.Headline.Contains("Bayou Flood Control Bond")));
        }

        [Test]
        public void PlayerCouncilMember_VoteIsCounted()
        {
            var seat = Civic.Officeholders.First(o => o.Office == Office.Council);
            seat.Person = _player.CharacterId;
            seat.IsPlayer = true;
            var gov = _world.Government;
            Assert.IsTrue(gov.Propose(_player.CharacterId, "youth_curfew", repeal: false).Success);
            Assert.IsTrue(gov.CastCouncilVote(_player, "youth_curfew", aye: true).Success);
            CivicDays(_world.Today, _world.Today + 8);
            var p = Civic.Proposals.Single(x => x.OrdinanceId == "youth_curfew");
            Assert.GreaterOrEqual(p.Ayes, 1);
            if (p.Passed)
            {
                Assert.IsTrue(gov.CurfewAt(23));
                Assert.IsTrue(gov.CurfewAt(2));
                Assert.IsFalse(gov.CurfewAt(12));
            }
        }

        [Test]
        public void Election_PlayerFilesCampaignsAndVotes_AndTheWinnerTakesOffice()
        {
            var day = _world.Today;
            _world.Government.ScheduleElections(day);
            var mayorRace = Civic.Elections.Single(e => e.Office == Office.Mayor);
            Assert.GreaterOrEqual(mayorRace.Candidates.Count, 2, "an incumbent and a challenger");

            var treasury = _world.Ledger.BalanceOf(_world.Accounts.Treasury);
            var filed = _world.Government.FileCandidacy(_player, Office.Mayor, "", Slates.Renewal, "Dana Reyes", "file-1");
            Assert.IsTrue(filed.Success, filed.Error);
            Assert.AreEqual(treasury + new Money(CivicService.FilingFeeCents), _world.Ledger.BalanceOf(_world.Accounts.Treasury));
            Assert.IsFalse(_world.Government.FileCandidacy(_player, Office.Mayor, "", Slates.Renewal, "Dana Reyes", "file-2").Success, "once");
            var me = mayorRace.Candidates.Single(c => c.IsPlayer);
            Assert.IsTrue(me.CampaignAccount.IsValid);

            var donor = NewPlayer(_world, 22, "Sam");
            Assert.IsFalse(_world.Government.Donate(donor, mayorRace.Id, me.Person, new Money(CivicService.DonationCapCents + 1), "don-0").Success, "capped");
            Assert.IsTrue(_world.Government.Donate(donor, mayorRace.Id, me.Person, Money.FromDollars(1000), "don-1").Success);
            Assert.AreEqual(Money.FromDollars(1000), _world.Ledger.BalanceOf(me.CampaignAccount));
            var recognition = me.Recognition;
            Assert.IsTrue(_world.Government.SpendCampaign(_player, mayorRace.Id, Money.FromDollars(900), "ads-1").Success);
            Assert.Greater(me.Recognition, recognition);
            Assert.IsFalse(_world.Government.SpendCampaign(_player, mayorRace.Id, Money.FromDollars(900), "ads-2").Success, "only what was raised");

            Assert.IsTrue(_world.Government.CastBallot(donor, mayorRace.Id, me.Person).Success);
            Assert.IsFalse(_world.Government.CastBallot(donor, mayorRace.Id, me.Person).Success, "one vote each");

            CivicDays(day + 1, mayorRace.ElectionDay);
            Assert.IsTrue(mayorRace.Held);
            Assert.IsFalse(Civic.Elections.Contains(mayorRace));
            Assert.Contains(mayorRace, Civic.PastElections);
            Assert.Greater(mayorRace.Turnout, 10, "NPCs vote");
            Assert.That(mayorRace.Turnout / (double)mayorRace.EligibleVoters, Is.InRange(0.2, 0.8), "a plausible municipal turnout");
            Assert.AreEqual(mayorRace.Turnout, mayorRace.Candidates.Sum(c => c.Votes));
            var mayor = Civic.Officeholders.Single(o => o.Office == Office.Mayor);
            Assert.AreEqual(mayorRace.Winner, mayor.Person);
            Assert.AreEqual(mayorRace.Candidates.OrderByDescending(c => c.Votes).First().Votes, mayorRace.Candidates.Single(c => c.Person == mayorRace.Winner).Votes);
            Assert.IsTrue(_world.History.Recent.Any(r => r.Headline.Contains("elected mayor")));
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }

        [Test]
        public void Elections_AreDeterministic()
        {
            Election Run()
            {
                var w = WorldGenerator.Create("civic", TestContent.DefaultConfig(), TestContent.Load(), new MemoryTransactionJournal());
                w.Government.ScheduleElections(w.Today);
                var e = w.Civic.Elections.Single(x => x.Office == Office.Mayor);
                for (var d = w.Today + 1; d <= e.ElectionDay; d++) w.Government.ProcessDay(d);
                return e;
            }
            var a = Run();
            var b = Run();
            Assert.AreEqual(a.Winner, b.Winner);
            CollectionAssert.AreEqual(a.Candidates.Select(c => c.Votes).ToList(), b.Candidates.Select(c => c.Votes).ToList());
        }

        [Test]
        public void PlayerElectionsDisabled_BlocksCandidacy()
        {
            _world.Config.Gameplay.PlayerElections = false;
            _world.Government.ScheduleElections(_world.Today);
            Assert.IsFalse(_world.Government.FileCandidacy(_player, Office.Mayor, "", Slates.Renewal, "Dana", "k").Success);
        }

        [Test]
        public void RegistrationOrdinance_MakesUnregisteredPublicUseACrime()
        {
            _world.Config.Powers.CollateralDamage = false;
            _world.Config.Gameplay.PvpEnabled = false;
            var p = new PowerInstance { Stage = PowerStage.Mastered, Progress = new PowerProgress { Experience = 700f, PrecisionBonus = 0.4f } };
            p.Definition.Components.Add(new PowerComponent { Domain = PowerDomain.Energy, Verb = EffectVerb.Shield, Delivery = DeliveryMode.Self, Element = PowerElement.Light, Magnitude = 1f, Range = 40f, Precision = 0.9f, Efficiency = 0.7f });
            _player.Powers.Powers.Add(p);
            var gov = _world.Government;
            Assert.IsFalse(gov.RegistrationRequired);
            gov.Enact(gov.Ordinance("anomaly_registration"));
            Assert.IsTrue(gov.RegistrationRequired);

            PowerOutcome Display()
            {
                PowerOutcome last = null;
                for (var i = 0; i < 40; i++)
                {
                    _player.Powers.Stamina = 1f;
                    _player.Powers.Strain = 0f;
                    p.CooldownUntilSecond = 0;
                    var here = Crowd();
                    last = _world.PowerUse.Use(_player, new PowerUseRequest { PowerIndex = 0, Target = TargetKind.Self, Origin = here, Point = here, Intensity = 1f });
                    if (last.Use.Success && last.Witnesses > 0) return last;
                    _world.Clock.AdvanceGame(3600);
                }
                Assert.Fail("Could not find a crowd.");
                return last;
            }

            var unregistered = Display();
            Assert.IsNotNull(unregistered.Crime);
            Assert.AreEqual("unregistered_anomalous_activity", unregistered.Crime.Incident.CrimeTypeId);

            Assert.IsTrue(gov.RegisterPowers(_player).Success);
            Assert.IsFalse(gov.RegisterPowers(_player).Success);
            var registered = Display();
            Assert.IsNull(registered.Crime, "registered persons may use abilities in public");
        }

        private WorldPosition Crowd()
        {
            _world.Director.Index.Update(_world.Clock.Now);
            foreach (var npc in _world.Population.Ordered)
            {
                if (!npc.Alive || !_world.Director.Index.TryGet(npc.Id, _world.Clock.Now, out var activity, out var pos)) continue;
                var place = _world.Geography.GetPlace(activity.Place);
                if (activity.Activity == ActivityKind.Commuting || place != null && (place.Kind == PlaceKind.Park || place.Kind == PlaceKind.Beach)) return pos;
            }
            return new WorldPosition();
        }

        [Test]
        public void Ripple_NpcsPostAboutRealEvents()
        {
            var district = _world.Geography.Districts.OrderBy(d => d.Id).First();
            var before = _world.Ripple.Posts.Count;
            _world.History.Record(_world.Today, HistoryCategory.Crime, 3, "Armed robbery at a corner store", "", district.Id);
            var posts = _world.Ripple.Posts.Skip(before).ToList();
            Assert.AreEqual(2, posts.Count, "importance 3 draws two posts");
            foreach (var p in posts)
            {
                Assert.AreEqual("crime", p.Topic);
                Assert.AreEqual(district.Id, p.District);
                Assert.IsFalse(p.Text.Contains("{"), "tokens are filled: " + p.Text);
                Assert.IsNotNull(_world.Population.Get(p.Author));
                Assert.LessOrEqual(p.Text.Length, RippleService.MaxPostLength);
                Assert.Contains("#PortArden", p.Tags);
            }
            _world.History.Record(_world.Today, HistoryCategory.Crime, 1, "Bike stolen");
            Assert.AreEqual(before + 2, _world.Ripple.Posts.Count, "trivia does not trend");
        }

        [Test]
        public void Ripple_PlayersPostLikeFollowAndReadAFeed()
        {
            var friend = NewPlayer(_world, 23, "Kai");
            Assert.IsFalse(_world.Feed.Post(_player, "Dana", "   ", out _).Success);
            Assert.IsFalse(_world.Feed.Post(_player, "Dana", new string('a', RippleService.MaxPostLength + 1), out _).Success);
            Assert.IsTrue(_world.Feed.Post(friend, "Kai", "Opening night at the pier! #PierLights", out var post).Success);
            CollectionAssert.AreEqual(new[] { "#PierLights" }, post.Tags);

            // Fill the city feed with noise, then check the followed post comes first.
            for (var i = 0; i < 5; i++) _world.History.Record(_world.Today, HistoryCategory.Anomaly, 4, "Strange lights #" + i);
            Assert.IsTrue(_world.Feed.Follow(_player, friend.CharacterId).Success);
            Assert.IsFalse(_world.Feed.Follow(_player, _player.CharacterId).Success);
            var feed = _world.Feed.Feed(_player.CharacterId, 10);
            Assert.AreEqual(post.Id, feed[0].Id);
            Assert.AreEqual(10, feed.Count);
            Assert.AreEqual(1, _world.Feed.FollowerCount(friend.CharacterId));

            Assert.IsTrue(_world.Feed.Like(_player, post.Id).Success);
            Assert.IsFalse(_world.Feed.Like(_player, post.Id).Success, "one like per person");
            Assert.AreEqual(1, post.Likes);
            Assert.AreSame(post, _world.Feed.Find(post.Id));

            var tagged = _world.Feed.Feed(_player.CharacterId, 10, "#pierlights");
            Assert.AreEqual(1, tagged.Count);
            Assert.IsTrue(_world.Feed.Trending().Any(t => t.tag == "#ArdenAnomaly"));

            _world.Feed.ProcessDay(_world.Today);
            Assert.Greater(post.Likes, 1, "NPCs engage with player posts at the end of the day");

            for (var i = 1; i < RippleService.MaxPlayerPostsPerDay; i++) Assert.IsTrue(_world.Feed.Post(friend, "Kai", "post " + i, out _).Success);
            Assert.IsFalse(_world.Feed.Post(friend, "Kai", "one too many", out _).Success);
        }

        [Test]
        public void Calendar_EventsSpanTheirDates_AndShiftDemand()
        {
            var cal = _world.Calendar;
            var carnival = GameDateTime.FromCalendar(2031, 2, 21, 12);
            Assert.IsTrue(cal.ActiveOn(carnival).Any(e => e.Id == "mardi_bayou"));
            Assert.IsFalse(cal.ActiveOn(GameDateTime.FromCalendar(2031, 2, 23, 12)).Any(e => e.Id == "mardi_bayou"), "three days: 20th to 22nd");
            Assert.IsTrue(cal.ActiveOn(GameDateTime.FromCalendar(2031, 12, 31, 12)).Any(e => e.Id == "winter_holidays"));
            Assert.AreEqual(1.6f, cal.DemandMultiplier(PlaceKind.Nightlife, GameDateTime.FromCalendar(2032, 1, 1, 12)), 1e-4f);
            Assert.AreEqual(1f, cal.DemandMultiplier(PlaceKind.Nightlife, GameDateTime.FromCalendar(2031, 3, 10, 12)), 1e-6f);
            Assert.Greater(cal.DemandMultiplier(PlaceKind.Shop, GameDateTime.FromCalendar(2031, 12, 22, 12)), 1.3f);

            var before = _world.History.Recent.Count;
            cal.AnnounceDay(GameDateTime.FromCalendar(2031, 10, 14).DayIndex);
            Assert.IsTrue(_world.History.Recent.Skip(before).Any(r => r.Headline.StartsWith("Founders' Day")));
        }

        [Test]
        public void ChemicalIncident_ClosesTheDistrictsBusinesses_AndIsCalledIn()
        {
            var district = _world.Geography.Districts.OrderBy(d => d.Id)
                .First(d => _world.Businesses.Values.Any(b => _world.Geography.GetPlace(b.Place)?.District == d.Id));
            _world.Clock.AdvanceGame(GameDateTime.SecondsPerDay - _world.Clock.Now.SecondOfDay + 3600); // 01:00 tomorrow
            var sim = new WorldSimulation(_world);
            sim.Update();
            var calls = _world.Emergency.Stats.Calls;
            _world.Calendar.Trigger(DisasterKind.ChemicalIncident, district, _world.Clock.Now);
            Assert.IsTrue(_world.Calendar.Closed(district.Id, _world.Clock.Now.TotalSeconds));
            Assert.Greater(_world.Emergency.Stats.Calls, calls);
            Assert.IsTrue(_player.Inbox.Any(m => m.Body.Contains("chemical release")));

            var day = _world.Today;
            sim.AdvanceDays(1);
            var inZone = _world.Businesses.Values.Where(b => _world.Geography.GetPlace(b.Place)?.District == district.Id).ToList();
            var outside = _world.Businesses.Values.Where(b => _world.Geography.GetPlace(b.Place)?.District != district.Id).ToList();
            Assert.IsTrue(inZone.All(b => b.Reports.Any(r => r.Day == day && r.WasClosed)), "closed by emergency order");
            Assert.IsTrue(outside.Any(b => b.Reports.Any(r => r.Day == day && !r.WasClosed)), "the rest of the city trades");

            sim.AdvanceDays(2);
            Assert.IsFalse(_world.Disasters.Active.Any(d => d.Kind == DisasterKind.ChemicalIncident), "the all-clear");
            Assert.IsTrue(_world.History.Recent.Any(r => r.Headline.StartsWith("All-clear")));
        }

        [Test]
        public void FlashFlood_DamagesPropertyOnlyInItsDistrict()
        {
            var district = _world.Geography.Districts.OrderByDescending(d => d.FloodRisk).ThenBy(d => d.Id).First();
            var before = _world.Properties.All.ToDictionary(p => p.Id, p => p.DamageRepairCents);
            _world.Calendar.Trigger(DisasterKind.FlashFlood, district, _world.Clock.Now);
            var changed = _world.Properties.All.Where(p => p.DamageRepairCents != before[p.Id]).ToList();
            Assert.IsNotEmpty(changed);
            Assert.IsTrue(changed.All(p => p.District == district.Id));
            Assert.IsTrue(_world.History.Recent.Any(r => r.Category == HistoryCategory.Disaster && r.District == district.Id));
        }

        [Test]
        public void Blackout_AccruesOutageHoursForItsDistrict()
        {
            var district = _world.Geography.Districts.OrderBy(d => d.Id).First();
            var t = _world.Clock.Now;
            _world.Calendar.Trigger(DisasterKind.Blackout, district, t);
            Assert.IsTrue(_world.Calendar.BlackoutAt(district.Id, t.TotalSeconds));
            for (var h = 0; h < 3; h++) _world.Calendar.ProcessHour(t.AddHours(h));
            Assert.AreEqual(3f, _world.Calendar.OutageHoursToday(district));
            var other = _world.Geography.Districts.OrderBy(d => d.Id).Skip(1).First();
            Assert.AreEqual(0f, _world.Calendar.OutageHoursToday(other));
        }

        [Test]
        public void CivicAndSocialState_SurviveSaveAndLoad()
        {
            var dir = TestContent.TempDirectory("civic-roundtrip");
            var saves = new WorldSaveSystem(dir);
            int posts, officeholders;
            using (var journal = saves.OpenJournal())
            {
                var config = TestContent.DefaultConfig();
                var w = WorldGenerator.Create("civic-save", config, TestContent.Load(), journal);
                var c = NewPlayer(w, 30, "Lee");
                w.Government.Enact(w.Government.Ordinance("sales_tax_increase"));
                w.Government.ScheduleElections(w.Today);
                w.Feed.Post(c, "Lee", "Hello Port Arden #hello", out _);
                w.History.Record(w.Today, HistoryCategory.Anomaly, 4, "Lights over the bay");
                w.Calendar.Trigger(DisasterKind.HeatWave, null, w.Clock.Now);
                posts = w.Ripple.Posts.Count;
                officeholders = w.Civic.Officeholders.Count;
                saves.Save(w);
            }
            using (var journal = saves.OpenJournal())
            {
                var load = saves.Load(TestContent.Load(), journal);
                Assert.IsFalse(load.Report.HasErrors, load.Report.ToString());
                var w = load.World;
                Assert.AreEqual(posts, w.Ripple.Posts.Count);
                Assert.AreEqual(officeholders, w.Civic.Officeholders.Count);
                Assert.IsTrue(w.Civic.IsActive("sales_tax_increase"));
                Assert.AreEqual(0.0875f, w.Config.Economy.SalesTaxRate, 1e-6f, "ordinance effects are re-applied to a fresh config on load");
                Assert.IsNotEmpty(w.Civic.Elections);
                Assert.IsTrue(w.Disasters.Active.Any(d => d.Kind == DisasterKind.HeatWave));
                Assert.IsTrue(w.Calendar.HeatWaveOn(w.Clock.Now));
                var next = w.Ripple.NextId;
                w.Feed.Post(w.Characters.Values.First(), "Lee", "again", out var again);
                Assert.AreEqual(next, again.Id, "post ids continue");
            }
        }

        [Test]
        public void YouthCurfew_KeepsMinorsHome_AndLiftsWithRepeal()
        {
            var adult = _world.Population.Ordered.First(n => n.Alive && n.AgeYears(_world.Today) >= 25 && n.Home.IsValid);
            // Find a teenager who is out between 23:00 and 05:00 on some night without a curfew.
            Core.Population.NpcRecord teen = null;
            GameDateTime? outLate = null;
            foreach (var n in _world.Population.Ordered.Where(n => n.Alive && n.Home.IsValid && n.AgeYears(_world.Today) >= 12 && n.AgeYears(_world.Today) <= 17))
            {
                for (var d = 0; d < 14 && outLate == null; d++)
                    for (var m = 23 * 60; m < 29 * 60 && outLate == null; m += 15)
                    {
                        var t = GameDateTime.FromCalendar(2030, 5, 7).AddDays(d).AddMinutes(m);
                        var a = _world.Schedules.Resolve(n, t).Activity;
                        if (a != Core.Population.ActivityKind.Sleeping && a != Core.Population.ActivityKind.AtHome && a != Core.Population.ActivityKind.Working) outLate = t;
                    }
                if (outLate != null) { teen = n; break; }
            }
            var gov = _world.Government;
            var adultLate = GameDateTime.FromCalendar(2030, 5, 8, 23, 30);
            var adultBefore = _world.Schedules.Resolve(adult, adultLate).Activity;
            gov.Enact(gov.Ordinance("youth_curfew"));
            Assert.AreEqual(adultBefore, _world.Schedules.Resolve(adult, adultLate).Activity, "adults are unaffected");
            Assert.IsNotNull(teen, "some teenager stays out late without a curfew");
            for (var d = 0; d < 20; d++)
                foreach (var hm in new[] { 23 * 60 + 15, 23 * 60 + 45, 24 * 60 + 90, 24 * 60 + 4 * 60 + 30 })
                {
                    var t = GameDateTime.FromCalendar(2030, 5, 7).AddDays(d).AddMinutes(hm);
                    var a = _world.Schedules.Resolve(teen, t, out var until).Activity;
                    Assert.That(a, Is.EqualTo(Core.Population.ActivityKind.Sleeping).Or.EqualTo(Core.Population.ActivityKind.AtHome).Or.EqualTo(Core.Population.ActivityKind.Working), "day " + d + " " + t);
                    Assert.Greater(until.TotalSeconds, t.TotalSeconds);
                }
            Assert.IsTrue(outLate.HasValue, "some teen evening out exists to test against");
            if (outLate.HasValue)
            {
                Assert.AreNotEqual(Core.Population.ActivityKind.Leisure, _world.Schedules.Resolve(teen, outLate.Value).Activity);
                gov.Repeal(gov.Ordinance("youth_curfew"));
                Assert.AreNotEqual(Core.Population.ActivityKind.AtHome, _world.Schedules.Resolve(teen, outLate.Value).Activity, "repeal lifts it");
            }
        }

        [Test]
        public void LeftoverCampaignFunds_CarryOverToTheNextRun()
        {
            _world.Government.ScheduleElections(_world.Today);
            var race = Civic.Elections.Single(e => e.Office == Office.Mayor);
            Assert.IsTrue(_world.Government.FileCandidacy(_player, Office.Mayor, "", Slates.Renewal, "Dana", "f1").Success);
            var account = race.Candidates.Single(c => c.IsPlayer).CampaignAccount;
            var donor = NewPlayer(_world, 24, "Lou");
            Assert.IsTrue(_world.Government.Donate(donor, race.Id, _player.CharacterId, Money.FromDollars(2000), "d1").Success);
            CivicDays(_world.Today + 1, race.ElectionDay);
            Assert.IsTrue(race.Held);
            Assert.AreEqual(Money.FromDollars(2000), _world.Ledger.BalanceOf(account), "unspent donations stay campaign money");

            _world.Government.ScheduleElections(_world.Today);
            Assert.IsTrue(_world.Government.FileCandidacy(_player, Office.Mayor, "", Slates.Renewal, "Dana", "f2").Success);
            var again = Civic.Elections.Single(e => e.Office == Office.Mayor).Candidates.Single(c => c.IsPlayer);
            Assert.AreEqual(account, again.CampaignAccount, "the same account, with the same money, for the next run");
            Assert.IsTrue(_world.Government.SpendCampaign(_player, Civic.Elections.Single(e => e.Office == Office.Mayor).Id, Money.FromDollars(1500), "ads").Success);
            Assert.IsTrue(_world.Ledger.VerifyInvariant(out _));
        }
    }
}
