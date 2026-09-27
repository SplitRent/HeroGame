using System;
using System.Collections.Generic;
using System.Diagnostics;
using HeroGame.Core.Business;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Time;
using HeroGame.Core.World;

namespace HeroGame.Core.Simulation
{
    /// <summary>Timing of the last simulation step, for the server inspector and dev reports.</summary>
    public sealed class SimulationStats
    {
        public double LastDayMilliseconds;
        public double LastPopulationMilliseconds;
        public double LastBusinessMilliseconds;
        public long DaysSimulated;
        public long HoursSimulated;
        public int NpcDaysLastDay;
    }

    /// <summary>
    /// Drives world time forward (TDD §7). Hourly: weather, outages, anomaly triggers. Daily (at
    /// midnight, for the day that just ended): macro economy, every NPC's life, social events,
    /// businesses, loans, property. The same code path runs live and during offline catch-up, so a
    /// server that was down for a week comes back with a week of believable history.
    /// </summary>
    public sealed class WorldSimulation
    {
        /// <summary>Upper bound on offline catch-up, to keep start-up bounded after very long downtime.</summary>
        public const int MaxCatchUpDays = 60;

        private readonly World _world;
        private LifeSimContext _lifeContext;
        public readonly SimulationStats Stats = new SimulationStats();

        public event Action<long> DayCompleted;
        public event Action<AnomalyEvent> AnomalyTriggered;

        public WorldSimulation(World world)
        {
            _world = world;
            _world.Social.Notable += OnNotableLife;
        }

        /// <summary>Simulates everything between the cursor and the world clock.</summary>
        public void Update()
        {
            var target = _world.Clock.Now;
            var cursor = _world.Cursor;
            var earliest = target.AddDays(-MaxCatchUpDays);
            if (cursor.SimulatedUpTo < earliest) cursor.SimulatedUpTo = earliest.StartOfDay;

            while (true)
            {
                var nextHour = new GameDateTime((cursor.SimulatedUpTo.HourIndex + 1) * GameDateTime.SecondsPerHour);
                if (nextHour > target)
                {
                    CheckPendingAnomaly(target);
                    cursor.SimulatedUpTo = target;
                    break;
                }
                cursor.SimulatedUpTo = nextHour;
                OnHour(nextHour);
                if (nextHour.SecondOfDay == 0) OnDayEnded(nextHour.DayIndex - 1);
            }
        }

        /// <summary>Convenience for tools/tests: move the clock and simulate.</summary>
        public void AdvanceDays(int days)
        {
            _world.Clock.AdvanceGame(days * GameDateTime.SecondsPerDay);
            Update();
        }

        private void OnHour(GameDateTime t)
        {
            Stats.HoursSimulated++;
            _world.Weather.AdvanceTo(t);
            var effects = _world.Weather.Effects;
            var c = _world.Cursor;
            c.DayBadWeatherSum += effects.BadWeather;
            c.DayHours++;
            var rng = DeterministicRandom.For(_world.Seed, 0x0A7A6E, (ulong)t.HourIndex);
            if (rng.Chance(effects.PowerOutageRiskPerHour)) c.DayOutageHours += 1f + rng.NextFloat() * 3f;
            CheckPendingAnomaly(t);
            _world.Wanted.Tick(t, null);
        }

        private void OnDayEnded(long day)
        {
            var sw = Stopwatch.StartNew();
            var date = new GameDateTime(day * GameDateTime.SecondsPerDay);
            _world.Dirty.MarkAll(new[] { SaveChunks.Meta, SaveChunks.Population, SaveChunks.Businesses, SaveChunks.Properties, SaveChunks.Environment });

            MacroEconomySimulator.StepDay(_world.Macro, _world.Seed, day);
            var effects = _world.Weather.Effects;
            if (effects.EmergencyDeclared) MacroEconomySimulator.ApplyShock(_world.Macro, -0.4);

            // --- Population.
            var popWatch = Stopwatch.StartNew();
            if (_lifeContext == null) _lifeContext = _world.CreateLifeContext();
            _lifeContext.Notable = OnNotableLife;
            var ordered = _world.Population.Ordered;
            var npcDays = 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                var npc = ordered[i];
                var workplace = npc.Workplace;
                var school = npc.School;
                npcDays += NpcLifeSimulator.CatchUp(npc, day, _lifeContext);
                if (npc.Workplace != workplace || npc.School != school) _world.Population.Reindex(npc);
            }
            _world.Social.DailyPass(_world.Population, day);
            foreach (var npc in _world.Population.Ordered) if (npc.LastSimulatedDay < day) npc.LastSimulatedDay = day; // newborns
            Stats.LastPopulationMilliseconds = popWatch.Elapsed.TotalMilliseconds;
            Stats.NpcDaysLastDay = npcDays;

            // --- Businesses.
            var bizWatch = Stopwatch.StartNew();
            var c = _world.Cursor;
            var badWeather = c.DayHours > 0 ? c.DayBadWeatherSum / c.DayHours : 0f;
            var ids = new List<EntityId>(_world.Businesses.Keys);
            ids.Sort();
            foreach (var id in ids)
            {
                var b = _world.Businesses[id];
                if (b.LastSimulatedDay >= day) continue;
                var place = _world.Geography.GetPlace(b.Place);
                var district = place != null ? _world.Geography.GetDistrict(place.District) : null;
                var report = _world.BusinessSim.SimulateDay(b, new BusinessDayContext
                {
                    Day = day,
                    DayOfWeek = date.DayOfWeek,
                    FootTraffic = district != null ? district.FootTraffic : 1f,
                    BadWeather = badWeather,
                    PowerOutageHours = c.DayOutageHours,
                    ForcedClosure = effects.EmergencyDeclared && _world.Weather.State.Current.Kind == Weather.WeatherKind.Hurricane,
                    CycleIndex = _world.Macro.CycleIndex,
                    PriceLevel = _world.Macro.PriceLevel,
                    RevenueMultiplier = _world.Config.Economy.BusinessRevenueMultiplier,
                    WageMultiplier = _world.Config.Economy.WageMultiplier,
                    ExternalAccount = _world.Accounts.External,
                    TreasuryAccount = _world.Accounts.Treasury,
                    Timestamp = date.AddHours(23),
                });
                if (b.ConsecutiveLossDays == 30)
                    _world.History.Record(day, HistoryCategory.Business, 2, b.Name + " struggling after a month of losses", report.Note, district != null ? district.Id : EntityId.None, b.Id);
            }
            Stats.LastBusinessMilliseconds = bizWatch.Elapsed.TotalMilliseconds;

            // --- Loans & property.
            var defaulted = _world.Loans.ProcessDuePayments(date.AddHours(23), _world.Transactions, _world.Ledger);
            foreach (var loan in defaulted)
            {
                if (loan.Collateral.IsValid && _world.Ownership.OwnerOf(loan.Collateral) == loan.Borrower)
                {
                    _world.Transactions.Execute(new WorldTransaction
                    {
                        Source = TransactionSource.Simulation,
                        Timestamp = date,
                        Description = "Repossession of " + loan.Collateral,
                        Ownership = { new OwnershipChange { Asset = loan.Collateral, From = loan.Borrower, To = _world.Accounts.BankOrganization } },
                    });
                    _world.History.Record(day, HistoryCategory.Economy, 2, "Bank repossesses property after loan default", "", EntityId.None, loan.Borrower, loan.Collateral);
                }
            }
            _world.Properties.ApplyDailyWear();
            if (day % 7 == 0) _world.Properties.Reassess(_world.Geography, _world.Macro, _world.Config.Economy.PropertyPriceMultiplier);

            // --- Anomalies: roll tomorrow's event now so it can fire live at its exact time.
            RollAnomaly(day + 1);
            foreach (var ch in _world.Characters.Values)
            {
                foreach (var started in PowerProgression.AdvanceDay(ch.Powers, day + 1))
                    _world.Events.Enqueue(new PowerManifestedEvent { Character = ch.CharacterId, Power = started });
            }
            foreach (var npc in _world.Population.Ordered)
                if (npc.Powers != null) PowerProgression.AdvanceDay(npc.Powers, day + 1);

            if (_world.Macro.InRecession && _world.Macro.DaysInCurrentPhase == 1)
                _world.History.Record(day, HistoryCategory.Economy, 4, "Economists warn " + _world.Config.Identity.CityName + " has entered a recession");

            c.DayBadWeatherSum = 0;
            c.DayHours = 0;
            c.DayOutageHours = 0;
            Stats.DaysSimulated++;
            Stats.LastDayMilliseconds = sw.Elapsed.TotalMilliseconds;
            DayCompleted?.Invoke(day);
            _world.Events.Flush();
        }

        private void RollAnomaly(long day)
        {
            if (_world.Cursor.PendingAnomaly != null) return;
            var districts = new List<(EntityId id, string type, WorldPosition center, float radius)>();
            foreach (var d in _world.Geography.Districts) districts.Add((d.Id, d.Type.ToString(), d.Center, d.Radius));
            districts.Sort((a, b) => a.id.CompareTo(b.id));
            _world.Cursor.PendingAnomaly = _world.Anomalies.RollDaily(day, districts, _world.Ids);
        }

        private void CheckPendingAnomaly(GameDateTime now)
        {
            var evt = _world.Cursor.PendingAnomaly;
            if (evt == null || evt.OccurredAt > now) return;
            _world.Cursor.PendingAnomaly = null;
            TriggerAnomaly(evt);
        }

        /// <summary>Applies an anomaly: exposes NPCs present (by schedule) and online characters.</summary>
        public void TriggerAnomaly(AnomalyEvent evt)
        {
            var day = evt.OccurredAt.DayIndex;
            var radiusSq = (evt.Radius + 150f) * (evt.Radius + 150f);
            var considered = new HashSet<EntityId>();
            foreach (var place in _world.Geography.Places)
            {
                if (WorldPosition.DistanceSquaredXZ(place.Position, evt.Position) > radiusSq) continue;
                foreach (var npcId in _world.Population.AssociatedWith(place.Id))
                {
                    if (!considered.Add(npcId)) continue;
                    var npc = _world.Population.Get(npcId);
                    if (npc == null || !npc.Alive) continue;
                    var activity = _world.Schedules.Resolve(npc, evt.OccurredAt);
                    if (!_world.Director.TryGetPosition(activity, out var pos)) continue;
                    var outdoors = activity.Activity == ActivityKind.Commuting || activity.Activity == ActivityKind.Leisure;
                    if (npc.Powers == null) npc.Powers = new CharacterPowers();
                    var result = _world.Anomalies.Expose(evt, new ExposureCandidate { Character = npcId, Position = pos, Openness = outdoors ? 1f : 0.35f, Powers = npc.Powers }, day);
                    if (npc.Powers.Exposures == 0 && npc.Powers.Powers.Count == 0) npc.Powers = null; // keep records lean
                    if (result == ExposureResult.GainedFirstPower || result == ExposureResult.GainedAdditionalPower)
                        npc.AddHistory(day, "anomaly_exposure", npc.FullName + " was near the incident and has felt strange since.");
                }
            }
            if (_world.OnlineCharacters != null)
                foreach (var candidate in _world.OnlineCharacters()) _world.Anomalies.Expose(evt, candidate, day);

            _world.AnomalyLog.Add(evt);
            var district = _world.Geography.GetDistrict(evt.District);
            _world.History.Record(day, HistoryCategory.Anomaly, 3,
                "Unexplained incident reported in " + (district != null ? district.Name : "the city"),
                evt.Description + " Witnesses describe " + string.Join(", ", evt.Phenomena).Replace('_', ' ') + ".",
                evt.District, evt.Id);
            _world.Dirty.Mark(SaveChunks.Environment);
            _world.Dirty.Mark(SaveChunks.Population);
            AnomalyTriggered?.Invoke(evt);
        }

        private void OnNotableLife(NpcRecord npc, LifeEvent e)
        {
            // Only a few life events are newsworthy; the rest stay in the NPC's own history.
            if (e.Kind == "death" && npc.IsImportant)
                _world.History.Record(e.Day, HistoryCategory.People, 3, e.Summary, "", EntityId.None, npc.Id);
            else if (e.Kind == "birth" || e.Kind == "married" || e.Kind == "death")
                _world.History.Record(e.Day, HistoryCategory.People, 1, e.Summary, "", EntityId.None, npc.Id);
        }
    }

    /// <summary>Raised when a character's latent power first produces symptoms.</summary>
    public sealed class PowerManifestedEvent
    {
        public EntityId Character;
        public PowerInstance Power;
    }
}
