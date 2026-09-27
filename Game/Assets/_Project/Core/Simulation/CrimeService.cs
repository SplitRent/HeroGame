using System;
using System.Collections.Generic;
using HeroGame.Core.Business;
using HeroGame.Core.Characters;
using HeroGame.Core.Crime;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Core.Population;
using HeroGame.Core.Property;
using HeroGame.Core.Vehicles;
using HeroGame.Core.World;
using PhoneCategory = HeroGame.Core.Phone.MessageCategory;

namespace HeroGame.Core.Simulation
{
    public sealed class CrimeResult
    {
        public bool Attempted;
        public bool Succeeded;
        /// <summary>Stopped in the act (staff/victim noticed): no loot, but the attempt is still a crime on record.</summary>
        public bool CaughtInAct;
        public bool Reported;
        public string Message = "";
        public CrimeIncident Incident;
        public Money Cash;
        public List<InventoryStack> Loot = new List<InventoryStack>();
        public int Witnesses;

        public static CrimeResult Refused(string why) => new CrimeResult { Message = why };
    }

    /// <summary>
    /// Player crime actions (GDD §31–32) resolved authoritatively in the core: who could see it (real NPCs at the
    /// place or nearby, cameras in the building's layout), whether it succeeds against the target's security, what
    /// is taken, and the evidence and police report that follow. Nothing here trusts the client beyond intent.
    /// </summary>
    public sealed class CrimeService
    {
        /// <summary>Share of retail value a fence pays for stolen goods (before reputation).</summary>
        public const double FenceRate = 0.32;
        public const double ChopShopRate = 0.18;

        private readonly World _w;

        public CrimeService(World world)
        {
            _w = world;
        }

        // ------------------------------------------------------------------ observers

        /// <summary>Everyone inside a place right now (schedules) plus its cameras (layout furniture).</summary>
        public List<Observer> ObserversInside(Place place, PropertyRecord property, EntityId exclude, DeterministicRandom rng)
        {
            var list = new List<Observer>();
            if (place != null)
                foreach (var id in _w.Director.PeopleAt(place, _w.Clock.Now))
                {
                    if (id == exclude) continue;
                    var npc = _w.Population.Get(id);
                    if (npc == null) continue;
                    list.Add(new Observer
                    {
                        Id = id,
                        Distance = 3f + (float)rng.NextDouble() * 12f,
                        Visibility = 0.9f,
                        Civic = npc.Personality.Conscientiousness,
                    });
                }
            var cameras = CameraCount(property);
            for (var i = 0; i < cameras; i++)
                list.Add(new Observer { Id = property.Id, IsCamera = true, Distance = 6f + i * 3f, Visibility = 1f, Monitored = property.HasAlarm });
            return list;
        }

        /// <summary>People on the street around a position (from the location index, so offline-consistent).</summary>
        public List<Observer> ObserversNear(WorldPosition position, float radius, EntityId exclude, float visibility)
        {
            var list = new List<Observer>();
            var ids = new HashSet<EntityId>();
            _w.Director.Index.Update(_w.Clock.Now);
            _w.Director.Index.Candidates(position, radius, ids);
            var sorted = new List<EntityId>(ids);
            sorted.Sort();
            foreach (var id in sorted)
            {
                if (id == exclude || !_w.Director.Index.TryGet(id, _w.Clock.Now, out var activity, out var pos)) continue;
                if (activity.Place.IsValid && _w.Geography.GetPlace(activity.Place) is Place p && p.Kind != PlaceKind.Park && p.Kind != PlaceKind.Beach
                    && p.Kind != PlaceKind.TransitStop && activity.Activity != ActivityKind.Commuting) continue; // indoors: can't see the street
                var d = WorldPosition.DistanceXZ(pos, position);
                if (d > radius) continue;
                var npc = _w.Population.Get(id);
                list.Add(new Observer { Id = id, Distance = d, Visibility = visibility, Civic = npc != null ? npc.Personality.Conscientiousness : 0.5f });
            }
            return list;
        }

        /// <summary>
        /// The person behind the counter. Business staffing is aggregate, so when no scheduled employee is on site the
        /// clerk is represented by the business itself — robberies always have at least this witness.
        /// </summary>
        private void AddClerk(BusinessRecord b, List<Observer> observers)
        {
            foreach (var id in b.Employees)
                foreach (var o in observers)
                    if (o.Id == id)
                    {
                        observers.Remove(o);
                        observers.Add(new Observer { Id = id, Distance = 1.5f, Visibility = 1f, Civic = o.Civic });
                        return;
                    }
            if (b.Staff > 0) observers.Add(new Observer { Id = b.Id, Distance = 1.5f, Visibility = 1f, Civic = 0.7f });
        }

        private int CameraCount(PropertyRecord property)
        {
            if (property == null) return 0;
            var n = 0;
            if (property.Layout != null)
                foreach (var f in property.Layout.Furniture)
                {
                    var def = _w.Construction.Validator.Item(f.CatalogId);
                    if (def != null && def.Tags.Contains("security_camera")) n++;
                }
            if (n == 0 && property.HasCameras) n = 1;
            return Math.Min(n, 4);
        }

        // ------------------------------------------------------------------ actions

        public CrimeResult Shoplift(ServerCharacter c, BusinessRecord b, ConcealmentState concealment)
        {
            if (Blocked(c, out var why)) return CrimeResult.Refused(why);
            var t = _w.BusinessSim.Template(b.TemplateId);
            var place = _w.Geography.GetPlace(b.Place);
            if (t == null || place == null) return CrimeResult.Refused("Nothing to take here.");
            if (!IsOpenNow(place)) return CrimeResult.Refused(b.Name + " is closed.");
            var property = _w.Properties.Get(b.Property);
            var rng = Rng(c, "shoplifting");
            var observers = ObserversInside(place, property, c.CharacterId, rng);
            var staffOnShift = Math.Min(b.Staff, 4);
            var detect = Math.Min(0.9, 0.1 + 0.07 * staffOnShift + 0.12 * CameraCount(property) + 0.01 * observers.Count);
            var result = Resolve(c, "shoplifting", place.District, place.Position, b.Id, observers, concealment, rng, detect);
            if (!result.Succeeded) return result;

            var item = RollItem(t.LootTag, rng);
            if (item != null) Give(c, item, result);
            // Shrinkage comes off the shelf.
            var normalDaily = t.BaseCustomersPerHour * BusinessSimulator.OpenHours(t) * t.AverageTicketCents;
            if (item != null && normalDaily > 0) b.InventoryDays = Math.Max(0f, b.InventoryDays - (float)(item.ValueCents / normalDaily));
            result.Message = item != null ? "You slipped out with " + item.DisplayName.ToLowerInvariant() + "." : "Nothing worth taking.";
            _w.Dirty.Mark(SaveChunks.Businesses);
            return result;
        }

        public CrimeResult Pickpocket(ServerCharacter c, NpcRecord victim, WorldPosition where, ConcealmentState concealment)
        {
            if (Blocked(c, out var why)) return CrimeResult.Refused(why);
            if (victim == null || !victim.Alive) return CrimeResult.Refused("There is nobody to rob.");
            var rng = Rng(c, "pickpocketing");
            var observers = ObserversNear(where, 25f, victim.Id, 0.8f);
            // The victim notices on their own, independent of bystanders.
            var detect = 0.2 + victim.Personality.Neuroticism * 0.2 + victim.Personality.Conscientiousness * 0.1;
            var result = Resolve(c, "pickpocketing", DistrictAt(where), where, victim.Id, observers, concealment, rng, detect, victimNpc: victim);
            if (!result.Succeeded) return result;

            var cash = Math.Min(Math.Max(0, victim.SavingsCents), 2000 + (long)(rng.NextDouble() * 18000));
            if (cash > 0)
            {
                // NPC cash is aggregate: it leaves the NPC's savings and enters the ledger from outside.
                victim.SavingsCents -= cash;
                if (!Pay(c, new Money(cash), "Pickpocketing")) cash = 0;
                result.Cash = new Money(cash);
            }
            if (rng.Chance(0.35))
            {
                var item = RollItem("pickpocket", rng);
                if (item != null && item.ValueCents > 0) Give(c, item, result);
            }
            result.Message = "Lifted " + result.Cash + (result.Loot.Count > 0 ? " and " + _w.Content.FindItem(result.Loot[0].ItemId).DisplayName.ToLowerInvariant() : "") + ".";
            _w.Dirty.Mark(SaveChunks.Population);
            return result;
        }

        /// <summary>Break into a building (needs a crowbar or lock-picks). Security, alarms, cameras and occupants all matter.</summary>
        public CrimeResult Burglary(ServerCharacter c, PropertyRecord p, ConcealmentState concealment)
        {
            if (Blocked(c, out var why)) return CrimeResult.Refused(why);
            if (p == null || p.Kind == PropertyKind.Land) return CrimeResult.Refused("There is nothing to break into.");
            if (_w.Ownership.IsOwnedBy(p.Id, c.CharacterId)) return CrimeResult.Refused("It's your own building.");
            var hasPicks = Has(c, "lockpick_set");
            if (!hasPicks && !Has(c, "crowbar")) return CrimeResult.Refused("You need a crowbar or lock-picks to get in.");
            var place = _w.Geography.GetPlace(p.Place);
            var residential = p.Zoning == ZoningType.Residential || p.Kind == PropertyKind.House || p.Kind == PropertyKind.Apartment || p.Kind == PropertyKind.Condo || p.Kind == PropertyKind.Mansion;
            var type = residential ? "burglary_residential" : "burglary_commercial";
            var rng = Rng(c, type);

            // Getting in: the strongest exterior door and the building's security level.
            var doorSecurity = 0;
            if (p.Layout != null)
                foreach (var o in p.Layout.Openings)
                {
                    var wall = p.Layout.FindWall(o.WallId);
                    if (wall != null && wall.Kind == Building.WallKind.Exterior && o.Kind != Building.OpeningKind.Window) doorSecurity = Math.Max(doorSecurity, o.Security);
                }
            var entry = 0.9 - 0.15 * Math.Max(doorSecurity, p.SecurityLevel) - (hasPicks ? 0 : 0.1);
            if (!rng.Chance(Math.Max(0.1, entry)))
            {
                // Failed to get in: noise and marks on the door; an alarm still goes off.
                _w.Properties.ApplyDamage(p, 0.005f);
                var failed = Resolve(c, type, p.District, place != null ? place.Position : default, p.Id,
                    ObserversNear(place != null ? place.Position : default, 30f, EntityId.None, 0.5f), concealment, rng, 1.0, alarm: p.HasAlarm);
                failed.Message = "The door held." + (p.HasAlarm ? " An alarm is ringing." : "");
                return failed;
            }

            var observers = ObserversInside(place, p, c.CharacterId, rng);
            var occupants = 0;
            foreach (var o in observers) if (!o.IsCamera) occupants++;
            var detect = Math.Min(0.95, 0.05 + 0.3 * occupants);
            var result = Resolve(c, type, p.District, place != null ? place.Position : default, p.Id, observers, concealment, rng, detect, alarm: p.HasAlarm);
            _w.Properties.ApplyDamage(p, 0.01f); // forced door/lock
            _w.Dirty.Mark(SaveChunks.Properties);
            if (!result.Succeeded) return result;

            if (residential)
            {
                var district = _w.Geography.GetDistrict(p.District);
                var tag = district != null && district.Wealth >= 0.6f ? "residence_high" : "residence_low";
                var count = 1 + rng.NextInt(0, 3);
                for (var i = 0; i < count; i++)
                {
                    var item = RollItem(tag, rng);
                    if (item != null) Give(c, item, result);
                }
            }
            else
            {
                BusinessRecord business = null;
                foreach (var b in _w.Businesses.Values) if (b.Property == p.Id) { business = b; break; }
                var t = business != null ? _w.BusinessSim.Template(business.TemplateId) : null;
                var tag = t != null ? t.LootTag : p.Kind == PropertyKind.Warehouse ? "warehouse" : "office";
                var item = RollItem(tag, rng);
                if (item != null) Give(c, item, result);
                if (business != null)
                {
                    // Takes the float left in the till overnight, if there is one (owners bank the rest).
                    var till = _w.Ledger.BalanceOf(business.Account).Cents;
                    var take = Math.Min(Math.Max(0, till), 20000 + (long)(rng.NextDouble() * 80000));
                    if (take > 0 && Steal(business.Account, c, new Money(take), "Burglary")) result.Cash = new Money(take);
                }
            }
            NotifyOwner(p.Id, "Your property at " + p.Address + " was broken into.");
            result.Message = "You got in and out" + (result.Loot.Count > 0 ? " with " + result.Loot.Count + " item(s)" : "") + (result.Cash.Cents > 0 ? " and " + result.Cash + " in cash" : "") + ".";
            return result;
        }

        /// <summary>Armed hold-up of a business: always seen by the staff, always reported; takes what is in the till.</summary>
        public CrimeResult RobStore(ServerCharacter c, BusinessRecord b, ConcealmentState concealment)
        {
            if (Blocked(c, out var why)) return CrimeResult.Refused(why);
            var place = _w.Geography.GetPlace(b.Place);
            if (place == null || !IsOpenNow(place)) return CrimeResult.Refused("There's nobody at the register.");
            if (_w.Ownership.IsOwnedBy(b.Id, c.CharacterId)) return CrimeResult.Refused("You can't rob your own business.");
            var property = _w.Properties.Get(b.Property);
            var rng = Rng(c, "store_robbery");
            var observers = ObserversInside(place, property, c.CharacterId, rng);
            AddClerk(b, observers);
            var result = Resolve(c, "store_robbery", place.District, place.Position, b.Id, observers, concealment, rng, detect: 0.0, alwaysReported: true);
            var till = _w.Ledger.BalanceOf(b.Account).Cents;
            var take = Math.Min(Math.Max(0, till), 30000 + (long)(rng.NextDouble() * 150000));
            if (take > 0 && Steal(b.Account, c, new Money(take), "Robbery")) result.Cash = new Money(take);
            b.Reputation = Math.Max(0f, b.Reputation - 0.02f);
            NotifyOwner(b.Id, b.Name + " was robbed at gunpoint. " + result.Cash + " was taken.");
            result.Message = "The clerk emptied the register: " + result.Cash + ".";
            _w.Dirty.Mark(SaveChunks.Businesses);
            return result;
        }

        /// <summary>Hot-wiring a parked vehicle. Alarms and immobilisers make it harder; the car stays legally the owner's.</summary>
        public CrimeResult StealVehicle(ServerCharacter c, VehicleRecord v, ConcealmentState concealment)
        {
            if (Blocked(c, out var why)) return CrimeResult.Refused(why);
            if (v == null || !v.Drivable) return CrimeResult.Refused("That vehicle isn't going anywhere.");
            if (_w.Ownership.IsOwnedBy(v.Id, c.CharacterId)) return CrimeResult.Refused("It's your car.");
            if (v.StolenBy == c.CharacterId) return CrimeResult.Refused("You already have it.");
            var model = _w.Vehicles.Model(v.ModelId);
            var security = SecurityMod(v);
            var rng = Rng(c, "vehicle_theft");
            var difficulty = 0.15 + Math.Min(0.35, (model != null ? model.BasePriceCents : 3000000) / 30000000.0)
                             + (security == "security_alarm" ? 0.15 : security == "security_immobilizer" ? 0.45 : 0);
            var observers = ObserversNear(v.Position, 30f, EntityId.None, 0.7f);
            var alarm = security == "security_alarm";
            var result = Resolve(c, "vehicle_theft", DistrictAt(v.Position), v.Position, v.Id, observers, concealment, rng, detect: difficulty * 0.6, alarm: alarm);
            if (!result.Succeeded)
            {
                result.Message = security == "security_immobilizer" ? "The immobiliser won't let it start." : alarm ? "The alarm went off." : "Someone saw you at the door.";
                return result;
            }
            // Plates are evidence that follows the car, not the thief.
            _w.Justice.Evidence.Add(new EvidenceItem { Kind = EvidenceKind.VehiclePlate, Incident = result.Incident.Id, Suspect = v.Id, Confidence = 1f, CollectedAt = _w.Clock.Now, Note = v.Plate });
            v.StolenBy = c.CharacterId;
            v.ReportedStolen = true;
            v.StolenSinceDay = _w.Today;
            NotifyOwner(v.Id, "Your vehicle " + v.Plate + " has been stolen. It has been reported to police; if it isn't recovered in 7 days your insurer can pay out.");
            _w.Dirty.Mark(SaveChunks.Vehicles);
            result.Message = "It started. Plates " + v.Plate + " will be on every patrol's list.";
            return result;
        }

        public CrimeResult Assault(ServerCharacter c, NpcRecord victim, WorldPosition where, ConcealmentState concealment, bool powered)
        {
            if (Blocked(c, out var why)) return CrimeResult.Refused(why);
            if (victim == null || !victim.Alive) return CrimeResult.Refused("There is nobody there.");
            var rng = Rng(c, powered ? "powered_assault" : "assault");
            var observers = ObserversNear(where, 35f, victim.Id, 0.9f);
            // The victim is always a witness.
            observers.Add(new Observer { Id = victim.Id, Distance = 1f, Visibility = 1f, Civic = 0.8f, KnowsPerpetrator = victim.MemoryOf(c.CharacterId, false, 0) != null });
            var result = Resolve(c, powered ? "powered_assault" : "assault", DistrictAt(where), where, victim.Id, observers, concealment, rng, detect: 0.0, victimNpc: victim);
            victim.Health = Math.Max(0.05f, victim.Health - (powered ? 0.35f : 0.15f));
            result.Message = powered ? "Your power sent them flying." : "You hit them.";
            _w.Dirty.Mark(SaveChunks.Population);
            return result;
        }

        public CrimeResult Vandalize(ServerCharacter c, PropertyRecord p, ConcealmentState concealment)
        {
            if (Blocked(c, out var why)) return CrimeResult.Refused(why);
            if (p == null) return CrimeResult.Refused("Nothing to damage.");
            var place = _w.Geography.GetPlace(p.Place);
            var pos = place != null ? place.Position : default;
            var rng = Rng(c, "vandalism");
            var observers = ObserversNear(pos, 30f, EntityId.None, 0.6f);
            var property = p;
            for (var i = 0; i < CameraCount(property); i++) observers.Add(new Observer { Id = p.Id, IsCamera = true, Distance = 10f, Visibility = 1f, Monitored = p.HasAlarm });
            var result = Resolve(c, "vandalism", p.District, pos, p.Id, observers, concealment, rng, detect: 0.0);
            _w.Properties.ApplyDamage(p, 0.02f + (float)rng.NextDouble() * 0.03f);
            _w.Dirty.Mark(SaveChunks.Properties);
            NotifyOwner(p.Id, "Vandals damaged your property at " + p.Address + ". Repairs: " + _w.Properties.RepairQuote(p, _w.Macro.PriceLevel) + ".");
            result.Message = "Glass and spray paint.";
            return result;
        }

        // ------------------------------------------------------------------ fencing

        public Money FenceOffer(ServerCharacter c)
        {
            long total = 0;
            var rate = FenceRateFor(c);
            foreach (var s in c.Inventory)
            {
                if (!s.Stolen) continue;
                var def = _w.Content.FindItem(s.ItemId);
                if (def != null) total += (long)(def.ValueCents * s.Quantity * rate * _w.Macro.PriceLevel * _w.Config.Economy.CrimePayoutMultiplier);
            }
            return new Money(total);
        }

        /// <summary>Fences pay more to people with a reputation in the underworld (up to +12 points).</summary>
        public double FenceRateFor(ServerCharacter c) => FenceRate + Math.Min(0.12, Math.Max(0, c.Reputation.Get(ReputationDimension.Criminal)) / 100.0 * 0.12);

        public OpResult SellToFence(ServerCharacter c, string idempotencyKey)
        {
            var offer = FenceOffer(c);
            if (offer.Cents <= 0) return OpResult.Fail("You have nothing a fence wants.");
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Fenced goods",
                Money = LedgerTransaction.Transfer(_w.Accounts.External, c.CheckingAccount, offer, TransactionReason.CrimeProceeds, "Cash sale"),
            });
            if (!result.Success) return result;
            c.Inventory.RemoveAll(s => s.Stolen);
            c.Reputation.Add(ReputationDimension.Criminal, 0.5f);
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
            return result;
        }

        /// <summary>A chop shop strips a stolen vehicle for parts; the car is gone for good.</summary>
        public OpResult ChopVehicle(ServerCharacter c, VehicleRecord v, string idempotencyKey)
        {
            if (v == null || v.StolenBy != c.CharacterId) return OpResult.Fail("The chop shop only takes cars you brought in.");
            var model = _w.Vehicles.Model(v.ModelId);
            if (model == null) return OpResult.Fail("Unknown vehicle.");
            var pay = new Money((long)(model.BasePriceCents * _w.Macro.PriceLevel * ChopShopRate * (0.5 + 0.5 * v.Condition) * _w.Config.Economy.CrimePayoutMultiplier));
            var result = _w.Transactions.Execute(new WorldTransaction
            {
                IdempotencyKey = idempotencyKey ?? "",
                Initiator = c.CharacterId,
                Timestamp = _w.Clock.Now,
                Description = "Chop shop",
                Money = LedgerTransaction.Transfer(_w.Accounts.External, c.CheckingAccount, pay, TransactionReason.CrimeProceeds, "Parts"),
            });
            if (!result.Success) return result;
            v.LocationKind = VehicleLocationKind.Destroyed;
            v.StolenBy = EntityId.None;
            _w.Dirty.Mark(SaveChunks.Vehicles);
            return result;
        }

        /// <summary>
        /// Files a crime committed by other means (powers, vehicles) with the given witnesses: evidence, report,
        /// police call and witness memories follow exactly as for the actions above.
        /// </summary>
        public CrimeResult CommitWitnessed(ServerCharacter c, string crimeId, WorldPosition where, EntityId victim, List<Observer> observers,
            ConcealmentState concealment, NpcRecord victimNpc = null) =>
            Resolve(c, crimeId, DistrictAt(where), where, victim, observers, concealment, Rng(c, crimeId), detect: 0.0, victimNpc: victimNpc);

        // ------------------------------------------------------------------ internals

        private bool Blocked(ServerCharacter c, out string why)
        {
            why = null;
            if (c.Record.InCustody) why = "You are in custody.";
            return why != null;
        }

        private bool IsOpenNow(Place place)
        {
            if (place.OpenMinute == place.CloseMinute) return true;
            var m = _w.Clock.Now.MinuteOfDay;
            return place.OpenMinute < place.CloseMinute ? m >= place.OpenMinute && m < place.CloseMinute : m >= place.OpenMinute || m < place.CloseMinute;
        }

        private DeterministicRandom Rng(ServerCharacter c, string crime) =>
            DeterministicRandom.For(_w.Seed, c.CharacterId.Value, (ulong)_w.Clock.Now.TotalSeconds, StableHash.Of(crime) ^ (ulong)_w.Justice.Incidents.Count);

        public EntityId DistrictAt(WorldPosition p)
        {
            District best = null;
            var bestD = float.MaxValue;
            foreach (var d in _w.Geography.Districts)
            {
                var dist = WorldPosition.DistanceXZ(d.Center, p);
                if (dist < bestD) { bestD = dist; best = d; }
            }
            return best != null ? best.Id : EntityId.None;
        }

        /// <summary>
        /// Common resolution: record the incident, decide success (not noticed in the act), run the witness model
        /// for evidence and a police report, update witness memories and the wanted state.
        /// </summary>
        private CrimeResult Resolve(ServerCharacter c, string crimeId, EntityId district, WorldPosition where, EntityId victim, List<Observer> observers,
            ConcealmentState concealment, DeterministicRandom rng, double detect, bool alarm = false, bool alwaysReported = false, NpcRecord victimNpc = null)
        {
            var type = _w.Content.FindCrime(crimeId);
            var incident = new CrimeIncident
            {
                Id = _w.Ids.Next(EntityKind.CrimeIncident),
                CrimeTypeId = crimeId,
                Perpetrator = c.CharacterId,
                Victim = victim,
                District = district,
                Position = where,
                OccurredAt = _w.Clock.Now,
            };
            var caught = rng.Chance(Math.Max(0.0, Math.Min(0.98, detect)));
            var d = _w.Geography.GetDistrict(district);
            var outcome = WitnessModel.Evaluate(incident, type, observers, concealment, d != null ? d.PoliceTrust : 0.6f, rng);
            foreach (var e in outcome.Evidence)
            {
                // Caught in the act: the person who stopped you had a good look.
                if (caught && !e.PointsToAlias) e.Confidence = Math.Max(e.Confidence, 0.5f * (1f - concealment.FaceConcealment) + 0.2f);
                _w.Justice.Evidence.Add(e);
                _w.Wanted.AddEvidence(e);
            }
            var reported = outcome.Reported || alarm || alwaysReported || (caught && rng.Chance(0.8));
            _w.Justice.Incidents.Add(incident);
            if (reported)
            {
                var poweredSuspect = type.Category == CrimeCategory.Anomalous;
                _w.Wanted.ReportCrime(incident, type, _w.Clock.Now, policeWitnessed: false, poweredSuspect: poweredSuspect);
                _w.Dispatch.ReportCrime(incident, type);
            }

            var witnesses = 0;
            foreach (var o in observers)
            {
                if (o.IsCamera) continue;
                var npc = _w.Population.Get(o.Id);
                if (npc == null) continue;
                _w.Conversations.RecordWitnessedCrime(npc, c.CharacterId, type.Severity, victimNpc != null && npc.Id == victimNpc.Id);
                witnesses++;
            }
            if (victimNpc != null && !observers.Exists(o => o.Id == victimNpc.Id))
                _w.Conversations.RecordWitnessedCrime(victimNpc, c.CharacterId, type.Severity, true);

            c.Reputation.Add(ReputationDimension.Criminal, 0.2f * type.Severity);
            if (type.Severity >= 6)
                _w.History.Record(_w.Today, HistoryCategory.Crime, Math.Min(5, type.Severity / 2), type.DisplayName + " reported" + (d != null ? " in " + d.Name : ""),
                    reported ? "Police are investigating." : "", district, incident.Id);
            _w.Dirty.Mark(SaveChunks.Justice);
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);

            return new CrimeResult
            {
                Attempted = true,
                Succeeded = !caught,
                CaughtInAct = caught,
                Reported = reported,
                Incident = incident,
                Witnesses = witnesses,
                Message = caught ? "You were spotted and had to bail." : "",
            };
        }

        private ItemDefinition RollItem(string tag, DeterministicRandom rng)
        {
            var pool = new List<ItemDefinition>();
            var weights = new List<double>();
            foreach (var i in _w.Content.Items)
            {
                if (!i.LootTags.Contains(tag)) continue;
                pool.Add(i);
                weights.Add(1.0 / Math.Sqrt(Math.Max(100, i.ValueCents))); // pricey items are rarer
            }
            var index = rng.PickWeighted(weights);
            return index < 0 ? null : pool[index];
        }

        private void Give(ServerCharacter c, ItemDefinition item, CrimeResult result)
        {
            var stack = new InventoryStack { ItemId = item.Id, Quantity = 1, Stolen = true };
            c.Inventory.Add(stack);
            result.Loot.Add(stack);
        }

        private bool Has(ServerCharacter c, string itemId)
        {
            foreach (var s in c.Inventory) if (s.ItemId == itemId && s.Quantity > 0) return true;
            return false;
        }

        private string SecurityMod(VehicleRecord v)
        {
            foreach (var m in v.Mods) if (m.Slot == ModSlot.Security) return m.ModId;
            return "";
        }

        private bool Pay(ServerCharacter c, Money amount, string memo) => _w.Transactions.Execute(new WorldTransaction
        {
            Initiator = c.CharacterId,
            Timestamp = _w.Clock.Now,
            Description = memo,
            Money = LedgerTransaction.Transfer(_w.Accounts.External, c.CheckingAccount, amount, TransactionReason.CrimeProceeds, memo),
        }).Success;

        private bool Steal(EntityId fromAccount, ServerCharacter c, Money amount, string memo) => _w.Transactions.Execute(new WorldTransaction
        {
            Initiator = c.CharacterId,
            Timestamp = _w.Clock.Now,
            Description = memo,
            Money = LedgerTransaction.Transfer(fromAccount, c.CheckingAccount, amount, TransactionReason.CrimeProceeds, memo),
        }).Success;

        private void NotifyOwner(EntityId asset, string text)
        {
            var owner = _w.Ownership.OwnerOf(asset);
            if (_w.Characters.TryGetValue(owner, out var ch)) _w.Phone.Send(ch, _w.Accounts.Government, "Police non-emergency", PhoneCategory.Emergency, text);
        }
    }
}
