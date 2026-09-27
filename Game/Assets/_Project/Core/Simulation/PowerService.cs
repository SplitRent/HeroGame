using System;
using System.Collections.Generic;
using HeroGame.Core.Characters;
using HeroGame.Core.Crime;
using HeroGame.Core.Emergency;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Property;
using HeroGame.Core.Vehicles;
using HeroGame.Core.World;

namespace HeroGame.Core.Simulation
{
    public sealed class PowerUseRequest
    {
        public int PowerIndex;
        /// <summary>0..1 how hard the player pushes (hold-to-charge).</summary>
        public float Intensity = 0.5f;
        public TargetKind Target = TargetKind.None;
        public EntityId TargetId;
        public WorldPosition Origin;
        /// <summary>Aim point for Point/Teleport/Blast targets.</summary>
        public WorldPosition Point;
    }

    public sealed class PowerOutcome
    {
        public bool Attempted;
        public string Message = "";
        public PowerUseResult Use;
        public EffectPlan Plan;
        public bool Hit;
        public int Affected;
        public float DamageDealt;
        public int Witnesses;
        public CrimeResult Crime;
        public bool Teleported;
        public WorldPosition TeleportTo;
        public List<InteractionResult> Interactions = new List<InteractionResult>();

        public static PowerOutcome Refused(string why) => new PowerOutcome { Message = why };
    }

    /// <summary>
    /// Executes power uses against the world (GDD §40–46): validity (stage, cooldown, custody, environment needs,
    /// range, server caps), the success roll and progression, the effect itself on people, vehicles and buildings
    /// (with element interactions and collateral), then consequences — witnesses, secret-identity clues, energy
    /// signatures, crimes, emergency calls, news. Deterministic per (seed, character, time), so the server's
    /// verdict can be reproduced from the request.
    /// </summary>
    public sealed class PowerService
    {
        public const float WitnessRadius = 60f;
        public const float RangeSlack = 3f;

        private readonly World _w;

        public PowerService(World world)
        {
            _w = world;
        }

        private long Now => _w.Clock.Now.TotalSeconds;

        private static bool Finite(WorldPosition p) =>
            !float.IsNaN(p.X) && !float.IsNaN(p.Y) && !float.IsNaN(p.Z) && !float.IsInfinity(p.X) && !float.IsInfinity(p.Y) && !float.IsInfinity(p.Z)
            && Math.Abs(p.X) < 100000f && Math.Abs(p.Z) < 100000f && Math.Abs(p.Y) < 10000f;

        public PowerOutcome Use(ServerCharacter c, PowerUseRequest r)
        {
            if (!_w.Config.Powers.PowersEnabled) return PowerOutcome.Refused("Powers are disabled on this server.");
            var powers = c.Powers;
            if (powers == null || r.PowerIndex < 0 || r.PowerIndex >= powers.Powers.Count) return PowerOutcome.Refused("You don't have that ability.");
            var power = powers.Powers[r.PowerIndex];
            if (power.Stage == PowerStage.Latent) return PowerOutcome.Refused("Nothing happens.");
            if (c.Record.InCustody) return PowerOutcome.Refused("Restraint collars in custody suppress your abilities.");
            if (c.Injury == InjuryState.Hospitalized || c.Injury == InjuryState.Incapacitated || c.Injury == InjuryState.Dead) return PowerOutcome.Refused("You can't.");
            if (power.CooldownUntilSecond > Now) return PowerOutcome.Refused("Still recovering from the last use.");
            var requirement = MissingRequirement(power, r);
            if (requirement != null) return PowerOutcome.Refused(requirement);
            if (r.Target == TargetKind.Character && !_w.Config.Gameplay.PvpEnabled) return PowerOutcome.Refused("PvP is disabled on this server.");
            if (!Finite(r.Origin) || !Finite(r.Point)) return PowerOutcome.Refused("Invalid position.");

            var intensity = Math.Max(0.05f, Math.Min(1f, float.IsNaN(r.Intensity) ? 0.5f : r.Intensity));
            var hasTarget = TryTargetPosition(r, out var targetPos);
            var distance = hasTarget ? WorldPosition.DistanceXZ(r.Origin, targetPos) : 0f;
            var preview = PowerEffects.Plan(power, intensity, distance);
            if (NeedsTarget(preview.Kind) && !hasTarget) return PowerOutcome.Refused("No target.");
            if (NeedsTarget(preview.Kind) && preview.Kind != EffectKind.Teleport && distance > preview.Range + RangeSlack) return PowerOutcome.Refused("Out of range.");

            var archetype = FindArchetype(power.Definition.ArchetypeId);
            var rng = DeterministicRandom.For(_w.Seed, c.CharacterId.Value, (ulong)Now, (ulong)(r.PowerIndex + 1) ^ (ulong)power.Progress.SuccessfulUses << 8 ^ (ulong)power.Progress.FailedUses << 24);
            var use = PowerProgression.Use(powers, power, archetype, intensity, rng);
            var output = Math.Min(use.Output, _w.Config.Powers.OutputCap);
            var plan = PowerEffects.Plan(power, output, distance);
            power.CooldownUntilSecond = Now + (long)Math.Ceiling(plan.CooldownSeconds * _w.Clock.TimeScale);
            var outcome = new PowerOutcome { Attempted = true, Use = use, Plan = plan };
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);

            var victims = new List<(TargetKind kind, EntityId id)>();
            if (!use.Success)
            {
                outcome.Message = use.Backfire ? "It gets away from you." : "It doesn't come.";
                if (use.Backfire)
                {
                    // Uncontrolled release: hurts the user and, for violent powers, whatever is around them.
                    Hurt(c, 0.05f + 0.15f * intensity, "a power backfire");
                    if (preview.Kind == EffectKind.Strike || preview.Kind == EffectKind.Blast)
                        Blast(c, r.Origin, 2f + 4f * intensity, 0.1f * intensity, power, outcome, victims);
                }
            }
            else
            {
                Apply(c, power, plan, r, hasTarget, targetPos, rng, outcome, victims);
            }
            if (use.StageAdvancedTo.HasValue) outcome.Message += " Something clicks: you understand it better now.";
            if (use.NewEvolution != null) outcome.Message += " A new way to use it occurs to you.";

            Consequences(c, power, plan, r, outcome, victims, use.Success || use.Backfire);
            return outcome;
        }

        private static bool NeedsTarget(EffectKind kind) =>
            kind == EffectKind.Strike || kind == EffectKind.Telekinesis || kind == EffectKind.Teleport;

        // ------------------------------------------------------------------ effects

        private void Apply(ServerCharacter c, PowerInstance power, EffectPlan plan, PowerUseRequest r, bool hasTarget, WorldPosition targetPos,
            DeterministicRandom rng, PowerOutcome outcome, List<(TargetKind, EntityId)> victims)
        {
            switch (plan.Kind)
            {
                case EffectKind.Strike:
                    outcome.Hit = rng.Chance(plan.Accuracy);
                    if (!outcome.Hit)
                    {
                        outcome.Message = "You miss.";
                        // A wild shot can still hit a building behind the target.
                        if (_w.Config.Powers.CollateralDamage && rng.Chance(0.3)) Blast(c, targetPos, 2f, plan.Damage * 0.3f, power, outcome, victims, peopleToo: false);
                        return;
                    }
                    HitTarget(c, r.Target, r.TargetId, plan.Damage, plan.Impulse, power, outcome, victims);
                    outcome.Message = "Direct hit.";
                    return;
                case EffectKind.Blast:
                    Blast(c, hasTarget ? targetPos : r.Origin, plan.Radius, plan.Damage, power, outcome, victims, exclude: c.CharacterId);
                    outcome.Message = outcome.Affected > 0 ? "The blast catches " + outcome.Affected + " target(s)." : "The blast rolls out over nothing.";
                    return;
                case EffectKind.Teleport:
                    var d = WorldPosition.DistanceXZ(r.Origin, targetPos);
                    if (d > plan.Range + RangeSlack)
                    {
                        outcome.Message = "You can't reach that far yet (" + plan.Range.ToString("0") + " m).";
                        return;
                    }
                    outcome.Teleported = true;
                    outcome.TeleportTo = targetPos;
                    c.LastPosition = targetPos;
                    outcome.Message = "You're there.";
                    return;
                case EffectKind.Heal:
                    if (r.Target == TargetKind.Npc && _w.Population.Get(r.TargetId) is NpcRecord npc)
                    {
                        npc.Health = Math.Min(1f, npc.Health + plan.Amount);
                        outcome.Affected = 1;
                    }
                    else if (r.Target == TargetKind.Character && _w.Characters.TryGetValue(r.TargetId, out var other)) Heal(other, plan.Amount);
                    else Heal(c, plan.Amount);
                    outcome.Message = "Warmth spreads through the wound.";
                    return;
                case EffectKind.Telekinesis:
                    if (r.Target == TargetKind.Vehicle && _w.Vehicles.Get(r.TargetId) is VehicleRecord v)
                    {
                        var model = _w.Vehicles.Model(v.ModelId);
                        if (model != null && model.MassKg > plan.MassKg)
                        {
                            outcome.Message = "It's too heavy for you (" + model.MassKg.ToString("0") + " kg).";
                            return;
                        }
                        v.Position = r.Point.X != 0 || r.Point.Z != 0 ? r.Point : v.Position;
                        if (_w.Config.Powers.CollateralDamage) _w.Vehicles.ApplyCollision(v, plan.Impulse * 2f, frontal: false);
                        victims.Add((TargetKind.Vehicle, v.Id));
                        outcome.Affected = 1;
                        outcome.Message = "The car lifts and slams down.";
                        _w.Dirty.Mark(SaveChunks.Vehicles);
                        return;
                    }
                    HitTarget(c, r.Target, r.TargetId, 0.05f, plan.Impulse, power, outcome, victims);
                    outcome.Message = "You shove them without touching them.";
                    return;
                case EffectKind.Sense:
                    var seen = new HashSet<EntityId>();
                    _w.Director.Index.Update(_w.Clock.Now);
                    _w.Director.Index.Candidates(r.Origin, plan.Radius, seen);
                    outcome.Affected = seen.Count;
                    outcome.Message = "You feel " + seen.Count + " people within " + plan.Radius.ToString("0") + " m.";
                    AddActive(c, EffectKind.Sense, plan.Radius, plan.Duration);
                    return;
                case EffectKind.Leap:
                    outcome.Message = "You jump " + plan.Amount.ToString("0") + " m.";
                    return;
                default:
                    AddActive(c, plan.Kind, plan.Amount, plan.Duration);
                    outcome.Message = plan.Kind + " active for " + plan.Duration.ToString("0") + " s.";
                    return;
            }
        }

        private void AddActive(ServerCharacter c, EffectKind kind, float amount, float realSeconds)
        {
            c.Powers.Active.RemoveAll(e => e.Kind == kind || e.UntilSecond <= Now);
            c.Powers.Active.Add(new ActivePowerEffect { Kind = kind, Amount = amount, UntilSecond = Now + (long)(realSeconds * _w.Clock.TimeScale) });
        }

        private void HitTarget(ServerCharacter c, TargetKind kind, EntityId id, float damage, float impulse, PowerInstance power, PowerOutcome outcome, List<(TargetKind, EntityId)> victims)
        {
            var element = power.Definition.PrimaryElement;
            switch (kind)
            {
                case TargetKind.Npc:
                    var npc = _w.Population.Get(id);
                    if (npc == null || !npc.Alive) return;
                    HurtNpc(npc, damage);
                    break;
                case TargetKind.Character:
                    if (!_w.Characters.TryGetValue(id, out var other) || other == c) return;
                    Hurt(other, damage, "an anomalous attack");
                    break;
                case TargetKind.Vehicle:
                    var v = _w.Vehicles.Get(id);
                    if (v == null || !_w.Config.Powers.CollateralDamage) return;
                    _w.Vehicles.ApplyCollision(v, impulse * 3f, frontal: true);
                    Interact(element, damage, PowerEffects.TagsFor(TargetKind.Vehicle, IsRaining(), false), outcome);
                    _w.Dirty.Mark(SaveChunks.Vehicles);
                    break;
                case TargetKind.Property:
                    var p = _w.Properties.Get(id);
                    if (p == null || !_w.Config.Powers.CollateralDamage) return;
                    DamageProperty(p, damage, element, outcome);
                    break;
                default:
                    return;
            }
            victims.Add((kind, id));
            outcome.Affected++;
            outcome.DamageDealt += damage;
        }

        private void Blast(ServerCharacter c, WorldPosition center, float radius, float damage, PowerInstance power, PowerOutcome outcome,
            List<(TargetKind, EntityId)> victims, EntityId exclude = default, bool peopleToo = true)
        {
            if (peopleToo)
            {
                var ids = new HashSet<EntityId>();
                _w.Director.Index.Update(_w.Clock.Now);
                _w.Director.Index.Candidates(center, radius, ids);
                var sorted = new List<EntityId>(ids);
                sorted.Sort();
                foreach (var id in sorted)
                {
                    if (!_w.Director.Index.TryGet(id, _w.Clock.Now, out _, out var pos)) continue;
                    var d = WorldPosition.DistanceXZ(pos, center);
                    if (d > radius) continue;
                    HitTarget(c, TargetKind.Npc, id, damage * (1f - 0.6f * d / radius), 0f, power, outcome, victims);
                }
                foreach (var other in _w.Characters.Values)
                {
                    if (other.CharacterId == exclude || other == c) continue;
                    var d = WorldPosition.DistanceXZ(other.LastPosition, center);
                    if (d <= radius && _w.Config.Gameplay.PvpEnabled) HitTarget(c, TargetKind.Character, other.CharacterId, damage * (1f - 0.6f * d / radius), 0f, power, outcome, victims);
                }
            }
            if (!_w.Config.Powers.CollateralDamage) return;
            foreach (var v in _w.Vehicles.All)
            {
                if (v.LocationKind != VehicleLocationKind.Street) continue;
                var d = WorldPosition.DistanceXZ(v.Position, center);
                if (d <= radius) HitTarget(c, TargetKind.Vehicle, v.Id, damage * (1f - 0.6f * d / radius), 1500f * damage, power, outcome, victims);
            }
            var places = new List<Place>();
            _w.Geography.QueryRadius(center, radius + 10f, places);
            places.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var place in places)
                if (place.Property.IsValid) HitTarget(c, TargetKind.Property, place.Property, damage * 0.5f, 0f, power, outcome, victims);
        }

        private void DamageProperty(PropertyRecord p, float damage, PowerElement element, PowerOutcome outcome)
        {
            _w.Properties.ApplyDamage(p, damage * 0.06f);
            _w.Dirty.Mark(SaveChunks.Properties);
            var results = Interact(element, damage, PowerEffects.TagsFor(TargetKind.Property, IsRaining(), false), outcome);
            foreach (var res in results)
                if (res.Rule.Outcome == InteractionOutcome.Ignite || res.Rule.Outcome == InteractionOutcome.Detonate)
                {
                    _w.Dispatch.ReportFire(p, Math.Min(0.6f, 0.1f + damage * 0.5f), "anomalous event");
                    break;
                }
        }

        private List<InteractionResult> Interact(PowerElement element, float magnitude, MaterialTag tags, PowerOutcome outcome)
        {
            var results = _w.Interactions.Resolve(element, magnitude, tags);
            outcome.Interactions.AddRange(results);
            return results;
        }

        private bool IsRaining() => _w.Weather.State.Current.Precipitation > 0.5f;

        private void HurtNpc(NpcRecord npc, float damage)
        {
            npc.Health = Math.Max(0.05f, npc.Health - damage);
            _w.Dirty.Mark(SaveChunks.Population);
            if (npc.Health > 0.3f) return;
            // Seriously hurt: someone calls an ambulance.
            if (_w.Director.Index.TryGet(npc.Id, _w.Clock.Now, out _, out var pos))
                _w.Dispatch.Report(EmergencyKind.Medical, pos, 1, npc.FullName + " injured", subject: npc.Id, severity: 1f - npc.Health);
        }

        /// <summary>Damage to a player, absorbed first by an active shield.</summary>
        public void Hurt(ServerCharacter c, float damage, string cause)
        {
            var shield = c.Powers?.ActiveOf(EffectKind.Shield, Now);
            if (shield != null)
            {
                var absorbed = Math.Min(shield.Amount, damage);
                shield.Amount -= absorbed;
                damage -= absorbed;
                if (shield.Amount <= 0f) shield.UntilSecond = Now;
            }
            if (damage <= 0f || !_w.Config.Gameplay.InjuriesEnabled) return;
            c.Health = Math.Max(0f, c.Health - damage);
            if (c.Health <= 0f) _w.Dispatch.CharacterDowned(c, c.LastPosition, Math.Min(1f, damage + 0.3f), cause);
            else if (c.Health < 0.5f && c.Injury == InjuryState.Healthy) c.Injury = InjuryState.Injured;
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
        }

        private void Heal(ServerCharacter c, float amount)
        {
            c.Health = Math.Min(1f, c.Health + amount);
            if (c.Health >= 0.5f && c.Injury == InjuryState.Injured) c.Injury = InjuryState.Healthy;
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + c.CharacterId);
        }

        // ------------------------------------------------------------------ consequences

        private void Consequences(ServerCharacter c, PowerInstance power, EffectPlan plan, PowerUseRequest r, PowerOutcome outcome,
            List<(TargetKind kind, EntityId id)> victims, bool visible)
        {
            if (!visible || !plan.Conspicuous && victims.Count == 0) return;
            var observers = _w.Crimes.ObserversNear(r.Origin, WitnessRadius, EntityId.None, 0.9f);
            observers.RemoveAll(o => victims.Exists(v => v.id == o.Id));
            outcome.Witnesses = observers.Count;
            var concealment = ConcealmentFor(c);

            // Bystanders who see a known person use a power learn something about who the costumed alias might be.
            if (c.Alias != null && !concealment.InAliasCostume && outcome.Witnesses > 0)
                foreach (var o in observers)
                    IdentityDiscovery.AddClue(c.Alias, o.Id, ClueKind.PowerSignatureMatch, _w.Clock.Now, 0.8f * Math.Min(1f, o.Visibility));

            if (outcome.Witnesses > 0)
                c.Reputation.Add(ReputationDimension.Notoriety, 0.2f * Math.Min(10, outcome.Witnesses) * (0.5f + outcome.Use.Output));

            // Harm to people or other people's property is a crime; the energy signature is evidence.
            string crime = null;
            EntityId victim = EntityId.None;
            NpcRecord victimNpc = null;
            foreach (var v in victims)
            {
                if (v.kind == TargetKind.Npc || v.kind == TargetKind.Character)
                {
                    crime = "powered_assault";
                    victim = v.id;
                    victimNpc = _w.Population.Get(v.id);
                    break;
                }
                if (crime == null && (v.kind == TargetKind.Property || v.kind == TargetKind.Vehicle) && !_w.Ownership.IsOwnedBy(v.id, c.CharacterId))
                {
                    crime = "anomalous_property_destruction";
                    victim = v.id;
                }
            }
            // Under the registration ordinance, any conspicuous public use by an unregistered person is itself an offence.
            if (crime == null && plan.Conspicuous && outcome.Witnesses > 0 && _w.Government.RegistrationRequired && !_w.Government.IsRegistered(c.CharacterId))
                crime = "unregistered_anomalous_activity";
            if (crime != null)
            {
                if (victimNpc != null) observers.Add(new Observer { Id = victimNpc.Id, Distance = 3f, Visibility = 1f, Civic = 0.8f });
                outcome.Crime = _w.Crimes.CommitWitnessed(c, crime, r.Origin, victim, observers, concealment, victimNpc);
                var signature = new EvidenceItem
                {
                    Kind = EvidenceKind.EnergySignature, Incident = outcome.Crime.Incident.Id, Suspect = c.CharacterId, Confidence = 0.3f,
                    CollectedAt = _w.Clock.Now, PointsToAlias = concealment.InAliasCostume, Note = power.Definition.PrimaryElement + " signature",
                };
                _w.Justice.Evidence.Add(signature);
                _w.Wanted.AddEvidence(signature);
            }

            // A big public display makes the news (once a day per person).
            if (outcome.Witnesses >= 3 && outcome.Use.Output >= 0.6f && c.Powers.LastNewsDay != _w.Today)
            {
                c.Powers.LastNewsDay = _w.Today;
                var district = _w.Geography.GetDistrict(_w.Crimes.DistrictAt(r.Origin));
                var who = concealment.InAliasCostume && c.Alias != null && !string.IsNullOrEmpty(c.Alias.Alias) ? c.Alias.Alias : "an unidentified person";
                _w.History.Record(_w.Today, HistoryCategory.Anomaly, 3,
                    "Witnesses describe " + ElementWords(power.Definition.PrimaryElement) + " display" + (district != null ? " in " + district.Name : ""),
                    outcome.Witnesses + " people saw " + who + " do something no one can explain.", district != null ? district.Id : EntityId.None, c.CharacterId);
            }
        }

        private static string ElementWords(PowerElement e)
        {
            switch (e)
            {
                case PowerElement.Fire: return "a fiery";
                case PowerElement.Electric: return "a crackling electrical";
                case PowerElement.Cold: return "a freezing";
                case PowerElement.Light: return "a blinding";
                case PowerElement.Kinetic: return "a violent kinetic";
                case PowerElement.Gravitic: return "a gravity-defying";
                case PowerElement.Sound: return "a deafening";
                default: return "an impossible";
            }
        }

        /// <summary>What witnesses can see: an active disguise power or alias costume hides the civilian identity.</summary>
        public ConcealmentState ConcealmentFor(ServerCharacter c)
        {
            var state = new ConcealmentState();
            var disguise = c.Powers?.ActiveOf(EffectKind.Disguise, Now);
            if (disguise != null) state.FaceConcealment = Math.Max(state.FaceConcealment, disguise.Amount);
            state.InAliasCostume = c.Alias != null && !string.IsNullOrEmpty(c.Alias.CostumeOutfitId) && c.CurrentOutfit == c.Alias.CostumeOutfitId;
            if (state.InAliasCostume) state.FaceConcealment = Math.Max(state.FaceConcealment, 0.8f);
            return state;
        }

        // ------------------------------------------------------------------ requirements & targets

        /// <summary>Environment limitations (GDD §42): some powers need water, darkness, metal or heat nearby.</summary>
        private string MissingRequirement(PowerInstance power, PowerUseRequest r)
        {
            foreach (var l in power.Definition.Limitations)
            {
                if (l.Kind != LimitationKind.EnvironmentRequirement || string.IsNullOrEmpty(l.Requirement)) continue;
                switch (l.Requirement)
                {
                    case "water":
                        var wet = IsRaining() || NearPlace(r.Origin, 80f, PlaceKind.Beach, PlaceKind.Dock);
                        if (!wet) return "You need water nearby.";
                        break;
                    case "darkness":
                        var h = _w.Clock.Now.Hour;
                        if (h >= 7 && h < 19) return "It only works in the dark.";
                        break;
                    case "heat":
                        if (_w.Weather.State.Current.TemperatureC < 30f && !NearFire(r.Origin)) return "It needs heat.";
                        break;
                    case "metal":
                        if (r.Target != TargetKind.Vehicle) return "It only works on metal.";
                        break;
                }
            }
            return null;
        }

        private bool NearPlace(WorldPosition p, float radius, params PlaceKind[] kinds)
        {
            var near = new List<Place>();
            _w.Geography.QueryRadius(p, radius, near);
            foreach (var place in near)
                foreach (var k in kinds)
                    if (place.Kind == k) return true;
            return false;
        }

        private bool NearFire(WorldPosition p)
        {
            foreach (var i in _w.Emergency.Incidents)
                if (i.Kind == EmergencyKind.Fire && i.Open && WorldPosition.DistanceXZ(i.Position, p) < 60f) return true;
            return false;
        }

        private bool TryTargetPosition(PowerUseRequest r, out WorldPosition pos)
        {
            pos = default;
            switch (r.Target)
            {
                case TargetKind.Self:
                    pos = r.Origin;
                    return true;
                case TargetKind.Point:
                    pos = r.Point;
                    return true;
                case TargetKind.Npc:
                    return _w.Director.Index.TryGet(r.TargetId, _w.Clock.Now, out _, out pos);
                case TargetKind.Character:
                    if (!_w.Characters.TryGetValue(r.TargetId, out var ch)) return false;
                    pos = ch.LastPosition;
                    return true;
                case TargetKind.Vehicle:
                    var v = _w.Vehicles.Get(r.TargetId);
                    if (v == null) return false;
                    pos = v.Position;
                    return true;
                case TargetKind.Property:
                    var p = _w.Properties.Get(r.TargetId);
                    var place = p != null ? _w.Geography.GetPlace(p.Place) : null;
                    if (place == null) return false;
                    pos = place.Position;
                    return true;
                default:
                    return false;
            }
        }

        private PowerArchetype FindArchetype(string id)
        {
            foreach (var a in _w.Content.PowerArchetypes) if (a.Id == id) return a;
            return null;
        }

        // ------------------------------------------------------------------ time

        /// <summary>Hourly recovery: stamina refills over ~4 game hours, strain eases, expired effects drop.</summary>
        public void Recover(long hours = 1)
        {
            foreach (var c in _w.Characters.Values)
            {
                var p = c.Powers;
                if (p == null) continue;
                p.Stamina = Math.Min(1f, p.Stamina + 0.25f * hours);
                p.Strain = Math.Max(0f, p.Strain - 0.08f * hours);
                p.Active.RemoveAll(e => e.UntilSecond <= Now);
            }
        }
    }
}
