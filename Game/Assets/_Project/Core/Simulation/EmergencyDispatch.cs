using System;
using System.Collections.Generic;
using HeroGame.Core.Characters;
using HeroGame.Core.Config;
using HeroGame.Core.Crime;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Property;
using HeroGame.Core.Time;
using HeroGame.Core.Vehicles;
using HeroGame.Core.World;
using PhoneCategory = HeroGame.Core.Phone.MessageCategory;
using Service = HeroGame.Core.Emergency.EmergencyService;

namespace HeroGame.Core.Simulation
{
    /// <summary>
    /// Emergency services (GDD §39–41). Calls become incidents; the dispatcher sends the free unit with the
    /// shortest road travel time (A* over the road graph with live congestion and weather, sirens included);
    /// units drive, work the scene and return. Fires grow, spread to neighbours and damage buildings until
    /// engines put them out; EMS treats and transports patients and the hospital bills them through their
    /// health cover; police on scene arrest a suspect who is still there. The city also generates its own
    /// background calls, so response times reflect real load. Event-driven, so it runs identically live
    /// (every frame) and in offline catch-up.
    /// </summary>
    public sealed class EmergencyDispatch
    {
        public const float ArrestRadius = 40f;
        public const long FireTickSeconds = 300;
        public const float SirenFactor = 0.72f;
        public const string HospitalName = "Bayside General Hospital";

        private readonly World _w;

        public EmergencyDispatch(World world)
        {
            _w = world;
        }

        private EmergencyState S => _w.Emergency;
        private long Now => _w.Clock.Now.TotalSeconds;

        // ------------------------------------------------------------------ units

        /// <summary>Crews every government police car, engine and ambulance; drops units whose vehicle is gone.</summary>
        public void EnsureUnits()
        {
            S.Units.RemoveAll(u => _w.Vehicles.Get(u.Vehicle) == null || _w.Vehicles.Get(u.Vehicle).LocationKind == VehicleLocationKind.Destroyed);
            // Only the city's own vehicles can be crewed: look at those, not every car in town.
            var crewed = new HashSet<EntityId>();
            foreach (var u in S.Units) crewed.Add(u.Vehicle);
            var vehicles = new List<VehicleRecord>();
            foreach (var asset in _w.Ownership.AssetsOf(_w.Accounts.Government))
            {
                if (asset.Kind != EntityKind.Vehicle || crewed.Contains(asset)) continue;
                var record = _w.Vehicles.Get(asset);
                if (record != null) vehicles.Add(record);
            }
            vehicles.Sort((a, b) => a.Id.CompareTo(b.Id));
            var counters = new Dictionary<Service, int>();
            foreach (var u in S.Units) counters[u.Service] = counters.TryGetValue(u.Service, out var n) ? n + 1 : 1;
            foreach (var v in vehicles)
            {
                var model = _w.Vehicles.Model(v.ModelId);
                if (model == null) continue;
                Service service;
                if (model.Class == VehicleClass.Police) service = Service.Police;
                else if (model.Class == VehicleClass.FireEngine) service = Service.Fire;
                else if (model.Class == VehicleClass.Ambulance) service = Service.Medical;
                else continue;
                counters[service] = counters.TryGetValue(service, out var c) ? c + 1 : 1;
                var station = NearestPlace(v.Position, service == Service.Police ? new[] { PlaceKind.PoliceStation }
                    : service == Service.Fire ? new[] { PlaceKind.FireStation } : new[] { PlaceKind.Hospital, PlaceKind.FireStation });
                S.Units.Add(new EmergencyUnit
                {
                    Vehicle = v.Id,
                    Service = service,
                    CallSign = (service == Service.Police ? "Adam-" : service == Service.Fire ? "Engine " : "Medic ") + counters[service],
                    Station = station != null ? station.Id : EntityId.None,
                    StationPosition = v.Position,
                    Status = UnitStatus.Available,
                });
            }
        }

        private Place NearestPlace(WorldPosition p, PlaceKind[] kinds)
        {
            Place best = null;
            var bestD = float.MaxValue;
            foreach (var k in kinds)
                foreach (var place in _w.Geography.PlacesOfKind(k))
                {
                    var d = WorldPosition.DistanceSquaredXZ(place.Position, p);
                    if (d < bestD) { bestD = d; best = place; }
                }
            return best;
        }

        // ------------------------------------------------------------------ calls

        public EmergencyIncident Report(EmergencyKind kind, WorldPosition position, int priority, string description,
            EntityId subject = default, EntityId crimeIncident = default, EntityId property = default, float severity = 0.5f)
        {
            var incident = NewIncident(kind, position, priority, description, subject, crimeIncident, property, severity);
            incident.Status = IncidentStatus.Queued;
            incident.ReportSecond = Now;
            S.Incidents.Add(incident);
            S.Stats.Calls++;
            if (kind == EmergencyKind.Fire) S.Stats.FiresStarted++;
            Dispatch(incident, Now);
            _w.Dirty.Mark(SaveChunks.Emergency);
            return incident;
        }

        private EmergencyIncident NewIncident(EmergencyKind kind, WorldPosition position, int priority, string description,
            EntityId subject, EntityId crimeIncident, EntityId property, float severity)
        {
            var incident = new EmergencyIncident
            {
                Id = _w.Ids.Next(EntityKind.EmergencyIncident),
                Kind = kind,
                Priority = Math.Max(1, Math.Min(5, priority)),
                Position = position,
                District = DistrictAt(position),
                Subject = subject,
                CrimeIncident = crimeIncident,
                Property = property,
                Description = description ?? "",
                Severity = severity,
            };
            switch (kind)
            {
                case EmergencyKind.Crime:
                    incident.PoliceNeeded = priority <= 1 ? 3 : priority == 2 ? 2 : 1;
                    break;
                case EmergencyKind.Fire:
                    incident.EnginesNeeded = 2;
                    incident.AmbulancesNeeded = priority <= 2 ? 1 : 0;
                    incident.FireIntensity = Math.Max(0.05f, severity);
                    break;
                case EmergencyKind.Medical:
                    incident.AmbulancesNeeded = 1;
                    break;
                case EmergencyKind.Collision:
                    incident.PoliceNeeded = 1;
                    incident.AmbulancesNeeded = severity > 0.3f ? 1 : 0;
                    break;
            }
            return incident;
        }

        /// <summary>Police are called for a reported crime (from the witness model, alarms or victims).</summary>
        public EmergencyIncident ReportCrime(CrimeIncident crime, CrimeType type)
        {
            var priority = type.Severity >= 6 ? 1 : type.Severity >= 4 ? 2 : 3;
            return Report(EmergencyKind.Crime, crime.Position, priority, type.DisplayName + " reported", crime.Perpetrator, crime.Id);
        }

        public EmergencyIncident ReportFire(PropertyRecord p, float intensity, string cause)
        {
            foreach (var i in S.Incidents)
                if (i.Kind == EmergencyKind.Fire && i.Property == p.Id && i.Open) return i;
            var place = _w.Geography.GetPlace(p.Place);
            var pos = place != null ? place.Position : default;
            var occupied = place != null && _w.Director.PeopleAt(place, _w.Clock.Now).Count > 0;
            return Report(EmergencyKind.Fire, pos, occupied ? 1 : 2, "Structure fire at " + p.Address + (string.IsNullOrEmpty(cause) ? "" : " (" + cause + ")"),
                property: p.Id, severity: intensity);
        }

        /// <summary>A player is badly hurt (combat, crash, fall): EMS comes; the server's death rule decides the rest.</summary>
        public EmergencyIncident CharacterDowned(ServerCharacter c, WorldPosition where, float severity, string cause)
        {
            c.Health = 0f;
            if (_w.Config.Gameplay.DeathRule == DeathRule.Permadeath)
            {
                c.Injury = InjuryState.Dead;
                _w.History.Record(_w.Today, HistoryCategory.People, 3, "A resident has died", cause, DistrictAt(where), c.CharacterId);
                _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
                return null;
            }
            c.Injury = InjuryState.Incapacitated;
            c.LastPosition = where;
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
            return Report(EmergencyKind.Medical, where, 1, "Person down: " + cause, subject: c.CharacterId, severity: Math.Max(0.3f, severity));
        }

        // ------------------------------------------------------------------ time

        /// <summary>Processes every emergency event up to <paramref name="t"/> in time order.</summary>
        public void AdvanceTo(GameDateTime t)
        {
            var target = t.TotalSeconds;
            if (S.SimulatedUpToSecond == 0) S.SimulatedUpToSecond = target;
            var guard = 0;
            while (S.SimulatedUpToSecond < target && guard++ < 100000)
            {
                var next = NextEventSecond(S.SimulatedUpToSecond, target);
                S.SimulatedUpToSecond = next;
                ProcessEventsAt(next);
            }
        }

        private long NextEventSecond(long after, long limit)
        {
            var next = limit;
            foreach (var i in S.Incidents)
            {
                if (i.Status == IncidentStatus.Scheduled && i.ReportSecond > after) next = Math.Min(next, i.ReportSecond);
                else if (i.Status == IncidentStatus.Scheduled) next = Math.Min(next, after + 1);
                if (i.Status == IncidentStatus.OnScene && i.ResolveSecond > 0) next = Math.Min(next, Math.Max(after + 1, i.ResolveSecond));
                if (i.Kind == EmergencyKind.Fire && i.Open && i.FireIntensity > 0) next = Math.Min(next, (after / FireTickSeconds + 1) * FireTickSeconds);
            }
            foreach (var u in S.Units)
                if (u.Status == UnitStatus.EnRoute || u.Status == UnitStatus.Transporting || u.Status == UnitStatus.Returning)
                    next = Math.Min(next, Math.Max(after + 1, u.ArriveSecond));
            foreach (var c in _w.Characters.Values)
                if (c.HospitalUntilSecond > after) next = Math.Min(next, c.HospitalUntilSecond);
            return Math.Max(after + 1, next);
        }

        private void ProcessEventsAt(long now)
        {
            foreach (var c in _w.Characters.Values)
                if (c.HospitalUntilSecond > 0 && now >= c.HospitalUntilSecond && (c.Injury == InjuryState.Hospitalized || c.Injury == InjuryState.Injured)) Discharge(c);

            // Background calls come in.
            foreach (var i in S.Incidents)
                if (i.Status == IncidentStatus.Scheduled && i.ReportSecond <= now)
                {
                    i.Status = IncidentStatus.Queued;
                    S.Stats.Calls++;
                    if (i.Kind == EmergencyKind.Fire) S.Stats.FiresStarted++;
                }

            // Units finishing trips.
            foreach (var u in S.Units)
            {
                if (u.ArriveSecond > now) continue;
                if (u.Status == UnitStatus.EnRoute) Arrive(u, now);
                else if (u.Status == UnitStatus.Transporting) Admit(u, now);
                else if (u.Status == UnitStatus.Returning)
                {
                    u.Status = UnitStatus.Available;
                    u.Route.Clear();
                }
            }

            // Scenes finishing.
            foreach (var i in S.Incidents)
                if (i.Status == IncidentStatus.OnScene && i.ResolveSecond > 0 && i.ResolveSecond <= now) FinishScene(i, now);

            // Fires burn on the tick.
            if (now % FireTickSeconds == 0)
            {
                var burning = new List<EmergencyIncident>();
                foreach (var i in S.Incidents) if (i.Kind == EmergencyKind.Fire && i.Open && i.FireIntensity > 0) burning.Add(i);
                foreach (var fire in burning) BurnTick(fire, now);
            }

            // Anyone waiting gets the next free unit, most urgent first.
            var queue = new List<EmergencyIncident>();
            foreach (var i in S.Incidents)
                if ((i.Status == IncidentStatus.Queued || i.Status == IncidentStatus.Dispatched || i.Status == IncidentStatus.OnScene) && NeedsMore(i)) queue.Add(i);
            queue.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.ReportSecond.CompareTo(b.ReportSecond));
            foreach (var i in queue) Dispatch(i, now);
            _w.Dirty.Mark(SaveChunks.Emergency);
        }

        private int Assigned(EmergencyIncident i, Service service)
        {
            var n = 0;
            foreach (var id in i.Units)
            {
                var u = S.Unit(id);
                if (u != null && u.Service == service && u.Incident == i.Id) n++;
            }
            return n;
        }

        private bool NeedsMore(EmergencyIncident i) =>
            Assigned(i, Service.Police) < i.PoliceNeeded || Assigned(i, Service.Fire) < i.EnginesNeeded || Assigned(i, Service.Medical) < i.AmbulancesNeeded;

        private void Dispatch(EmergencyIncident i, long now)
        {
            Send(i, Service.Police, i.PoliceNeeded - Assigned(i, Service.Police), now);
            Send(i, Service.Fire, i.EnginesNeeded - Assigned(i, Service.Fire), now);
            Send(i, Service.Medical, i.AmbulancesNeeded - Assigned(i, Service.Medical), now);
        }

        private void Send(EmergencyIncident i, Service service, int count, long now)
        {
            for (var k = 0; k < count; k++)
            {
                // Shortlist the closest free units in a straight line, then compare real road ETAs.
                var candidates = new List<(EmergencyUnit unit, float d)>();
                foreach (var u in S.Units)
                    if (u.Service == service && u.Free) candidates.Add((u, WorldPosition.DistanceXZ(u.PositionAt(now), i.Position)));
                if (candidates.Count == 0) return;
                candidates.Sort((a, b) => a.d != b.d ? a.d.CompareTo(b.d) : a.unit.Vehicle.CompareTo(b.unit.Vehicle));
                EmergencyUnit best = null;
                var bestEta = long.MaxValue;
                List<int> bestRoute = null;
                for (var c = 0; c < candidates.Count && c < 3; c++)
                {
                    var eta = TravelSeconds(candidates[c].unit.PositionAt(now), i.Position, now, out var route);
                    if (eta < bestEta) { bestEta = eta; best = candidates[c].unit; bestRoute = route; }
                }
                best.From = best.PositionAt(now);
                best.To = i.Position;
                best.Route = bestRoute ?? new List<int>();
                best.DepartSecond = now;
                best.ArriveSecond = now + bestEta;
                best.Status = UnitStatus.EnRoute;
                best.Incident = i.Id;
                i.Units.Add(best.Vehicle);
                if (i.Status == IncidentStatus.Queued) i.Status = IncidentStatus.Dispatched;
            }
        }

        /// <summary>Road travel time with live congestion and weather, sped up by lights and sirens.</summary>
        public long TravelSeconds(WorldPosition from, WorldPosition to, long now, out List<int> route)
        {
            route = null;
            var roads = _w.Roads;
            var straight = WorldPosition.DistanceXZ(from, to);
            if (roads == null || roads.Nodes.Count == 0) return 60 + (long)(straight / 12f);
            var a = roads.NearestNode(from);
            var b = roads.NearestNode(to);
            var time = new GameDateTime(now);
            var weather = _w.Weather.Effects;
            route = a == b ? new List<int> { a } : roads.Route(a, b, e => _w.Traffic.TravelSeconds(e, time, weather));
            if (route == null) return 60 + (long)(straight / 10f);
            double seconds = 0;
            for (var n = 0; n + 1 < route.Count; n++)
            {
                var edge = roads.EdgeBetween(route[n], route[n + 1]);
                seconds += edge != null ? _w.Traffic.TravelSeconds(edge, time, weather) : 0;
            }
            seconds = seconds * SirenFactor
                      + WorldPosition.DistanceXZ(from, roads.Nodes[a].Position) / 10f
                      + WorldPosition.DistanceXZ(roads.Nodes[b].Position, to) / 10f
                      + 45; // crew turnout
            return Math.Max(30, (long)Math.Round(seconds));
        }

        // ------------------------------------------------------------------ scenes

        private void Arrive(EmergencyUnit u, long now)
        {
            u.Status = UnitStatus.OnScene;
            u.CallsAnswered++;
            var i = S.Get(u.Incident);
            if (i == null || i.Status == IncidentStatus.Resolved)
            {
                Return(u, now);
                return;
            }
            if (i.Status != IncidentStatus.OnScene)
            {
                i.Status = IncidentStatus.OnScene;
                S.Stats.Responded++;
                S.Stats.TotalResponseSeconds += now - i.ReportSecond;
            }
            switch (i.Kind)
            {
                case EmergencyKind.Crime:
                    if (u.Service == Service.Police) CrimeScene(i, now);
                    break;
                case EmergencyKind.Medical:
                case EmergencyKind.Collision:
                    if (u.Service == Service.Medical) i.ResolveSecond = Math.Max(i.ResolveSecond, now + 900);
                    else i.ResolveSecond = Math.Max(i.ResolveSecond, now + 1800);
                    break;
                case EmergencyKind.Fire:
                    break; // resolved by the fire model
            }
        }

        private void CrimeScene(EmergencyIncident i, long now)
        {
            if (i.ResolveSecond == 0) i.ResolveSecond = now + 1500; // statements, evidence, report
            if (!_w.Characters.TryGetValue(i.Subject, out var suspect) || suspect.Record.InCustody) return;
            // The suspect is still here: officers make the arrest.
            if (WorldPosition.DistanceXZ(suspect.LastPosition, i.Position) > ArrestRadius) return;
            var arrest = _w.Courts.Arrest(suspect, caughtInPursuit: true, resisted: false);
            if (!arrest.Success) return;
            S.Stats.ArrestsOnScene++;
            i.Outcome = "Suspect arrested on scene.";
        }

        private void FinishScene(EmergencyIncident i, long now)
        {
            if (i.Kind == EmergencyKind.Medical || (i.Kind == EmergencyKind.Collision && i.AmbulancesNeeded > 0))
            {
                // The ambulance crew takes the patient in.
                foreach (var id in i.Units)
                {
                    var u = S.Unit(id);
                    if (u == null || u.Service != Service.Medical || u.Status != UnitStatus.OnScene || u.Incident != i.Id) continue;
                    var hospital = NearestPlace(i.Position, new[] { PlaceKind.Hospital });
                    var to = hospital != null ? hospital.Position : u.StationPosition;
                    var eta = TravelSeconds(i.Position, to, now, out var route);
                    u.From = i.Position;
                    u.To = to;
                    u.Route = route ?? new List<int>();
                    u.DepartSecond = now;
                    u.ArriveSecond = now + eta;
                    u.Status = UnitStatus.Transporting;
                    i.Status = IncidentStatus.Resolved;
                    i.ClosedSecond = now;
                    i.Outcome = "Patient transported to " + HospitalName + ".";
                    ReleaseOthers(i, now, except: u);
                    return;
                }
            }
            if (i.Kind == EmergencyKind.Crime && string.IsNullOrEmpty(i.Outcome))
            {
                // Background (NPC) crimes: sometimes the suspect is found nearby.
                var rng = DeterministicRandom.For(_w.Seed, i.Id.Value, 0xA2E57);
                var located = !_w.Characters.ContainsKey(i.Subject) && rng.Chance(0.3);
                i.Outcome = located ? "Suspect located and arrested." : "Report taken; investigation continues.";
                // A named resident (e.g. a mugger) really is arrested: a record and a few days in custody.
                var npc = located ? _w.Population.Get(i.Subject) : null;
                if (npc != null && npc.Alive)
                {
                    npc.Arrests++;
                    npc.HasCriminalRecord = true;
                    npc.OverrideActivity = Population.ActivityKind.InCustody;
                    npc.OverridePlace = NearestPlace(i.Position, new[] { PlaceKind.PoliceStation })?.Id ?? EntityId.None;
                    npc.OverrideUntilDay = _w.Today + 2 + rng.NextInt(0, 5);
                    npc.AddHistory(_w.Today, "arrested", npc.FullName + " was arrested.");
                    _w.Director.Invalidate(npc.Id);
                    if (i.CrimeIncident.IsValid)
                        foreach (var ci in _w.Justice.Incidents)
                            if (ci.Id == i.CrimeIncident) ci.Solved = true;
                    _w.Dirty.Mark(SaveChunks.Population);
                }
            }
            Close(i, now);
        }

        private void Close(EmergencyIncident i, long now)
        {
            i.Status = IncidentStatus.Resolved;
            i.ClosedSecond = now;
            ReleaseOthers(i, now, except: null);
        }

        private void ReleaseOthers(EmergencyIncident i, long now, EmergencyUnit except)
        {
            foreach (var id in i.Units)
            {
                var u = S.Unit(id);
                if (u == null || u == except || u.Incident != i.Id) continue;
                Return(u, now);
            }
        }

        private void Return(EmergencyUnit u, long now)
        {
            var from = u.PositionAt(now);
            // Driving back at normal speed: no sirens, no turnout time.
            var eta = Math.Max(30, (long)((TravelSeconds(from, u.StationPosition, now, out var route) - 45) / SirenFactor));
            u.From = from;
            u.To = u.StationPosition;
            u.Route = route ?? new List<int>();
            u.DepartSecond = now;
            u.ArriveSecond = now + eta;
            u.Status = UnitStatus.Returning;
            u.Incident = EntityId.None;
        }

        private void Admit(EmergencyUnit u, long now)
        {
            var i = S.Get(u.Incident);
            Return(u, now);
            if (i == null) return;
            S.Stats.PatientsTransported++;
            if (!_w.Characters.TryGetValue(i.Subject, out var c)) return;
            var days = MedicalBilling.StayDays(i.Severity);
            c.Injury = days > 0 ? InjuryState.Hospitalized : InjuryState.Injured;
            c.HospitalUntilSecond = now + Math.Max(1, days) * GameDateTime.SecondsPerDay / (days > 0 ? 1 : 8);
            var hospital = NearestPlace(i.Position, new[] { PlaceKind.Hospital });
            if (hospital != null) c.LastPosition = hospital.Position;
            if (_w.Config.Gameplay.DeathRule == DeathRule.HospitalWithLoss)
                c.Inventory.RemoveAll(s => s.Stolen || (_w.Content.FindItem(s.ItemId) is ItemDefinition d && !d.Legal));
            Bill(c, i.Severity);
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
        }

        /// <summary>Hospital bill: health cover pays its share to the hospital; what the patient cannot pay becomes medical debt.</summary>
        public Money Bill(ServerCharacter c, float severity)
        {
            var bill = new Money(MedicalBilling.Bill(severity, ambulance: true, _w.Macro.PriceLevel));
            var key = "hospital:" + c.CharacterId + ":" + Now;
            var paid = _w.Finance.PayMedicalBill(c, bill, _w.Accounts.HospitalAccount, "Hospital bill", key, out var covered);
            if (!paid.Success)
            {
                // Patient cannot cover their share: the insurer still pays its part; the rest goes on account.
                var policy = _w.Insurance.ActiveHealth(c.CharacterId);
                var insurerShare = 0L;
                if (policy != null)
                {
                    var deductibleLeft = Math.Max(0, policy.DeductibleCents - policy.DeductibleMetCents);
                    insurerShare = Math.Min(policy.RemainingCoverageCents, (long)Math.Round(Math.Max(0, bill.Cents - deductibleLeft) * policy.CoverShare));
                    policy.DeductibleMetCents += Math.Min(bill.Cents, deductibleLeft);
                    policy.PaidOutCents += insurerShare;
                    if (insurerShare > 0) policy.Claims++;
                }
                var patientShare = bill.Cents - insurerShare;
                var cash = Math.Max(0, Math.Min(patientShare, _w.Ledger.BalanceOf(c.CheckingAccount).Cents));
                var money = new Economy.LedgerTransaction { Reason = Economy.TransactionReason.Purchase, Memo = "Hospital bill" };
                money.Add(_w.Accounts.HospitalAccount, insurerShare + cash);
                if (insurerShare > 0) money.Add(_w.Accounts.InsurerAccount, -insurerShare);
                if (cash > 0) money.Add(c.CheckingAccount, -cash);
                if (insurerShare + cash > 0)
                    _w.Transactions.Execute(new Economy.WorldTransaction { Initiator = c.CharacterId, Timestamp = _w.Clock.Now, Description = "Hospital bill (partial)", Money = money });
                c.MedicalDebtCents += patientShare - cash;
                covered = new Money(insurerShare);
            }
            _w.Phone.Send(c, _w.Accounts.Hospital, HospitalName, PhoneCategory.Bank,
                "Hospital bill " + bill + (covered.Cents > 0 ? ", insurance paid " + covered : ", no health cover on file")
                + (c.MedicalDebtCents > 0 ? ". Outstanding medical debt: " + new Money(c.MedicalDebtCents) + "." : "."));
            return bill;
        }

        // ------------------------------------------------------------------ fire

        private void BurnTick(EmergencyIncident fire, long now)
        {
            var p = _w.Properties.Get(fire.Property);
            if (p == null)
            {
                fire.FireIntensity = 0f;
                Close(fire, now);
                return;
            }
            var engines = 0;
            foreach (var id in fire.Units)
            {
                var u = S.Unit(id);
                if (u != null && u.Service == Service.Fire && u.Status == UnitStatus.OnScene && u.Incident == fire.Id) engines++;
            }
            var weather = _w.Weather.State.Current;
            var growth = 0.035f * (1f + weather.WindSpeedMs / 25f) * (weather.Precipitation > 2f ? 0.5f : 1f);
            fire.FireIntensity = Math.Max(0f, Math.Min(1f, fire.FireIntensity + growth - 0.09f * engines));
            if (fire.FireIntensity > 0f) _w.Properties.ApplyDamage(p, fire.FireIntensity * 0.012f);
            _w.Dirty.Mark(SaveChunks.Properties);

            if (p.Damage == DamageState.Destroyed)
            {
                fire.FireIntensity = 0f;
                fire.Outcome = "Building lost.";
                S.Stats.BuildingsLost++;
                _w.History.Record(_w.Today, HistoryCategory.Disaster, 4, "Fire destroys " + p.Address, "", p.District, p.Id);
                Close(fire, now);
                NotifyOwner(p, "Fire destroyed your property at " + p.Address + ".");
                return;
            }
            if (fire.FireIntensity <= 0f)
            {
                fire.Outcome = "Fire extinguished.";
                Close(fire, now);
                NotifyOwner(p, "Firefighters put out a fire at " + p.Address + ". Damage: " + _w.Properties.RepairQuote(p, _w.Macro.PriceLevel) + ".");
                return;
            }

            // Spread to neighbours: keyed by (seed, fire, tick) so it is order-independent and replayable.
            var neighbours = new List<Place>();
            _w.Geography.QueryRadius(fire.Position, 30f, neighbours);
            neighbours.Sort((a, b) => a.Id.CompareTo(b.Id));
            var rng = DeterministicRandom.For(_w.Seed, fire.Id.Value, (ulong)now, 0xF1EE);
            foreach (var place in neighbours)
            {
                if (!place.Property.IsValid || place.Property == p.Id) continue;
                var target = _w.Properties.Get(place.Property);
                if (target == null || target.Damage == DamageState.Destroyed || target.Kind == PropertyKind.Land) continue;
                var chance = fire.FireIntensity * 0.02f * (1f + weather.WindSpeedMs / 15f) * Math.Max(0.2f, 1f - WorldPosition.DistanceXZ(place.Position, fire.Position) / 30f);
                if (rng.Chance(chance)) ReportFire(target, 0.1f, "spread from next door");
            }
        }

        private void NotifyOwner(PropertyRecord p, string text)
        {
            if (_w.Characters.TryGetValue(_w.Ownership.OwnerOf(p.Id), out var c)) _w.Phone.Send(c, _w.Accounts.Government, "Fire department", PhoneCategory.Emergency, text);
        }

        // ------------------------------------------------------------------ daily

        /// <summary>
        /// Schedules tomorrow's background calls per district (crime from the district's crime baseline, medical from
        /// its population, occasional fires) and discharges patients. Deterministic per (seed, district, day).
        /// </summary>
        public void ProcessDay(long day)
        {
            EnsureUnits();
            if (S.LastBackgroundDay >= day + 1) return;
            S.LastBackgroundDay = day + 1;

            var census = _w.Census;
            var districts = new List<District>(_w.Geography.Districts);
            districts.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var d in districts)
            {
                var pop = census.ResidentsOf(d.Id);
                var places = new List<Place>();
                foreach (var p in _w.Geography.Places) if (p.District == d.Id) places.Add(p);
                if (places.Count == 0) continue;
                places.Sort((a, b) => a.Id.CompareTo(b.Id));
                var rng = DeterministicRandom.For(_w.Seed, d.Id.Value, (ulong)(day + 1), 0xB6C0);
                // Rates: ~30 reported crimes and ~130 EMS calls per 1,000 residents a year; ~0.7 % of buildings burn a year.
                var crimes = Poisson(rng, d.CrimeBaseline * (pop + 50) / 2500.0 * _w.Config.Gameplay.CrimeSeverityMultiplier);
                var medical = Poisson(rng, pop * 0.00035 + 0.02);
                var fires = Poisson(rng, places.Count * 0.00002);
                for (var k = 0; k < crimes; k++) Schedule(EmergencyKind.Crime, places[rng.NextInt(0, places.Count)], day + 1, rng);
                for (var k = 0; k < medical; k++) Schedule(EmergencyKind.Medical, places[rng.NextInt(0, places.Count)], day + 1, rng);
                for (var k = 0; k < fires; k++)
                {
                    var place = places[rng.NextInt(0, places.Count)];
                    if (place.Property.IsValid) Schedule(EmergencyKind.Fire, place, day + 1, rng);
                }
            }
            S.Trim();
            _w.Dirty.Mark(SaveChunks.Emergency);
        }

        private void Discharge(ServerCharacter c)
        {
            c.Injury = InjuryState.Healthy;
            c.Health = 1f;
            c.HospitalUntilSecond = 0;
            c.Injury = InjuryState.Healthy;
            _w.Phone.Send(c, _w.Accounts.Hospital, HospitalName, PhoneCategory.Personal, "You have been discharged. Take it easy.");
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
        }

        private void Schedule(EmergencyKind kind, Place place, long day, DeterministicRandom rng)
        {
            string description;
            var priority = 3;
            var severity = 0.3f + (float)rng.NextDouble() * 0.5f;
            switch (kind)
            {
                case EmergencyKind.Crime:
                    var type = PickBackgroundCrime(rng);
                    description = type.DisplayName + " reported at " + place.Name;
                    priority = type.Severity >= 6 ? 1 : type.Severity >= 4 ? 2 : 3;
                    break;
                case EmergencyKind.Medical:
                    description = "Medical call at " + place.Name;
                    priority = severity > 0.6f ? 1 : 2;
                    break;
                default:
                    description = "Structure fire at " + place.Name;
                    priority = 2;
                    severity = 0.1f;
                    break;
            }
            var incident = NewIncident(kind, place.Position, priority, description, EntityId.None, EntityId.None, kind == EmergencyKind.Fire ? place.Property : EntityId.None, severity);
            incident.Status = IncidentStatus.Scheduled;
            incident.ReportSecond = day * GameDateTime.SecondsPerDay + rng.NextInt(0, (int)GameDateTime.SecondsPerDay);
            S.Incidents.Add(incident);
        }

        private CrimeType PickBackgroundCrime(DeterministicRandom rng)
        {
            var weights = new List<double>();
            foreach (var c in _w.Content.CrimeTypes)
                weights.Add(c.Category == CrimeCategory.Anomalous || c.Category == CrimeCategory.Major ? 0 : c.ReportLikelihood / (c.Severity * c.Severity));
            var index = rng.PickWeighted(weights);
            return _w.Content.CrimeTypes[Math.Max(0, index)];
        }

        private static int Poisson(DeterministicRandom rng, double lambda)
        {
            if (lambda <= 0) return 0;
            var l = Math.Exp(-Math.Min(lambda, 30));
            var k = 0;
            var p = 1.0;
            do
            {
                k++;
                p *= rng.NextDouble();
            } while (p > l && k < 100);
            return k - 1;
        }

        private EntityId DistrictAt(WorldPosition p)
        {
            District best = null;
            var bestD = float.MaxValue;
            foreach (var d in _w.Geography.Districts)
            {
                var dist = WorldPosition.DistanceSquaredXZ(d.Center, p);
                if (dist < bestD || (dist == bestD && best != null && d.Id.CompareTo(best.Id) < 0)) { bestD = dist; best = d; }
            }
            return best != null ? best.Id : EntityId.None;
        }
    }
}
