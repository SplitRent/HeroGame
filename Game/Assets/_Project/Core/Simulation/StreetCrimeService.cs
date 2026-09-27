using System;
using System.Collections.Generic;
using HeroGame.Core.Characters;
using HeroGame.Core.Combat;
using HeroGame.Core.Crime;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Population;
using HeroGame.Core.World;

namespace HeroGame.Core.Simulation
{
    public enum EncounterOutcome { Pending, Complied, Robbed, FoughtOff, Escaped }

    /// <summary>A mugging in progress: a real local NPC demands money from a player.</summary>
    public sealed class StreetEncounter
    {
        public long Id;
        public EntityId Character;
        public EntityId Mugger;
        public string MuggerName = "";
        public WorldPosition Position;
        public long DemandCents;
        public string WeaponId = "fists";
        public long StartedSecond;
        public long DeadlineSecond;
        public EncounterOutcome Outcome;

        public bool Open => Outcome == EncounterOutcome.Pending;
    }

    /// <summary>
    /// The city pushes back: at night, in rougher districts, someone may try to rob a player who is out on foot
    /// (only players actually present are eligible). Hand it over (the money moves through the ledger and the crime is
    /// reported), refuse (they attack: fighting back is self-defence, and knocking them down ends it), or get clear
    /// (<see cref="EscapeRadius"/>). Ignoring them past the deadline is refusing. Muggers are real residents and can be
    /// arrested later. Encounters are short-lived and not saved.
    /// </summary>
    public sealed class StreetCrimeService
    {
        public const float EscapeRadius = 25f;
        public const long ResponseSeconds = 30;
        public const long CooldownSeconds = 3 * 86400L;
        public const long MaxDemandCents = 30000;

        private readonly World _w;
        private readonly Dictionary<EntityId, StreetEncounter> _active = new Dictionary<EntityId, StreetEncounter>();
        private readonly Dictionary<EntityId, long> _lastEncounter = new Dictionary<EntityId, long>();
        private long _nextId;

        /// <summary>Which characters are out in the world right now (connected players / the local player). Null: nobody.</summary>
        public Func<ServerCharacter, bool> IsPresent;

        public event Action<StreetEncounter> Started;
        public event Action<StreetEncounter> Ended;

        public StreetCrimeService(World world)
        {
            _w = world;
        }

        private long Now => _w.Clock.Now.TotalSeconds;

        public StreetEncounter ActiveFor(EntityId character) =>
            _active.TryGetValue(character, out var e) && e.Open ? e : null;

        /// <summary>Hourly: maybe start an encounter for each present player, weighted by district, hour and how flush they look.</summary>
        public void ProcessHour(Time.GameDateTime t)
        {
            if (IsPresent == null || !_w.Config.Gameplay.StreetCrimeAgainstPlayers) return;
            var ids = new List<EntityId>(_w.Characters.Keys);
            ids.Sort();
            foreach (var id in ids)
            {
                var c = _w.Characters[id];
                if (!IsPresent(c) || ActiveFor(id) != null || c.Record.InCustody || c.Injury != InjuryState.Healthy && c.Injury != InjuryState.Injured) continue;
                if (_lastEncounter.TryGetValue(id, out var last) && Now - last < CooldownSeconds) continue;
                var rng = DeterministicRandom.For(_w.Seed, id.Value, (ulong)t.HourIndex, 0x5A1C);
                if (rng.Chance(Chance(c, t))) Start(c, c.LastPosition, rng);
            }
        }

        /// <summary>Probability of an encounter this hour.</summary>
        public double Chance(ServerCharacter c, Time.GameDateTime t)
        {
            var district = _w.Geography.GetDistrict(_w.Crimes.DistrictAt(c.LastPosition));
            if (district == null) return 0;
            var hour = t.Hour;
            var night = hour >= 21 || hour < 4 ? 1.0 : hour >= 18 || hour < 6 ? 0.4 : 0.08;
            var cash = _w.Ledger.BalanceOf(c.CheckingAccount).Cents;
            var flush = cash > 500000 ? 1.4 : cash > 50000 ? 1.0 : 0.6;
            var lit = _w.Destructibles.LightingAt(c.LastPosition, t) < 1f ? 1.5 : 1.0; // broken street lights invite trouble
            return Math.Min(0.25, 0.06 * district.CrimeBaseline * night * flush * lit * _w.Config.Gameplay.CrimeSeverityMultiplier);
        }

        /// <summary>Starts an encounter now (used by the hourly roll, tests and the admin console).</summary>
        public StreetEncounter Start(ServerCharacter c, WorldPosition at, DeterministicRandom rng = null)
        {
            rng = rng ?? DeterministicRandom.For(_w.Seed, c.CharacterId.Value, (ulong)Now, 0x5A1D);
            var mugger = PickMugger(at, rng);
            if (mugger == null) return null;
            var cash = _w.Ledger.BalanceOf(c.CheckingAccount).Cents;
            var e = new StreetEncounter
            {
                Id = ++_nextId,
                Character = c.CharacterId,
                Mugger = mugger.Id,
                MuggerName = mugger.FullName,
                Position = at,
                DemandCents = Math.Max(2000, Math.Min(MaxDemandCents, cash / 10 / 100 * 100)),
                WeaponId = rng.Chance(0.45) ? "folding_knife" : "fists",
                StartedSecond = Now,
                DeadlineSecond = Now + ResponseSeconds,
            };
            _active[c.CharacterId] = e;
            _lastEncounter[c.CharacterId] = Now;
            // A demand with menace is itself a threat: the player may defend themselves from the first moment.
            _w.Combat.State.Aggression[(mugger.Id, c.CharacterId)] = Now;
            _w.Phone.Send(c, EntityId.None, mugger.FirstName, Phone.MessageCategory.Emergency,
                "Someone steps out of the dark" + (e.WeaponId == "fists" ? "" : " with a knife") + ": \"" + new Money(e.DemandCents) + ". Now.\"");
            Started?.Invoke(e);
            return e;
        }

        /// <summary>Hand the money over: it leaves your account and the robbery is reported to the police.</summary>
        public OpResult Comply(ServerCharacter c, string idempotencyKey)
        {
            var e = ActiveFor(c.CharacterId);
            if (e == null) return OpResult.Fail("Nobody is asking you for anything.");
            var amount = new Money(Math.Min(e.DemandCents, Math.Max(0, _w.Ledger.BalanceOf(c.CheckingAccount).Cents)));
            if (amount.Cents > 0)
            {
                var r = Take(c, amount, idempotencyKey);
                if (!r.Success) return r;
            }
            Finish(e, EncounterOutcome.Complied, amount);
            return OpResult.Ok();
        }

        /// <summary>Say no: they attack. Fight (self-defence) or run.</summary>
        public OpResult Refuse(ServerCharacter c)
        {
            var e = ActiveFor(c.CharacterId);
            if (e == null) return OpResult.Fail("Nobody is asking you for anything.");
            Assault(c, e);
            return OpResult.Ok();
        }

        /// <summary>Called with the player's movement and every tick: escaping, deadlines and a downed mugger end encounters.</summary>
        public void Update(ServerCharacter c, WorldPosition position)
        {
            var e = ActiveFor(c.CharacterId);
            if (e == null) return;
            var mugger = _w.Population.Get(e.Mugger);
            if (mugger == null || !mugger.Alive || mugger.Health <= 0.3f || _w.Combat.IsStunned(mugger.Id))
            {
                Finish(e, EncounterOutcome.FoughtOff, Money.Zero);
                return;
            }
            if (WorldPosition.DistanceXZ(position, e.Position) > EscapeRadius)
            {
                Finish(e, EncounterOutcome.Escaped, Money.Zero);
                return;
            }
            if (Now >= e.DeadlineSecond) Assault(c, e);
        }

        /// <summary>The mugger attacks; if the player is badly hurt the money is taken anyway.</summary>
        private void Assault(ServerCharacter c, StreetEncounter e)
        {
            var mugger = _w.Population.Get(e.Mugger);
            if (mugger == null) return;
            var weapon = _w.Content.FindWeapon(e.WeaponId) ?? _w.Content.FindWeapon("fists");
            // From now on the player hitting back is self-defence.
            _w.Combat.State.Aggression[(mugger.Id, c.CharacterId)] = Now;
            var rng = DeterministicRandom.For(_w.Seed, (ulong)e.Id, (ulong)Now, 0x5A1E);
            if (rng.Chance(weapon.Accuracy)) _w.PowerUse.Hurt(c, weapon.Damage * (0.8f + 0.4f * rng.NextFloat()), weapon.DisplayName);
            e.DeadlineSecond = Now + 5; // they keep at it every few seconds until it's over
            if (c.Health < 0.35f || c.Injury == InjuryState.Incapacitated)
            {
                var amount = new Money(Math.Min(e.DemandCents * 2, Math.Max(0, _w.Ledger.BalanceOf(c.CheckingAccount).Cents)));
                if (amount.Cents > 0) Take(c, amount, "mugging:" + e.Id + ":taken");
                Finish(e, EncounterOutcome.Robbed, amount);
            }
        }

        private OpResult Take(ServerCharacter c, Money amount, string key) => _w.Transactions.Execute(new WorldTransaction
        {
            IdempotencyKey = key ?? "",
            Initiator = c.CharacterId,
            Timestamp = _w.Clock.Now,
            Description = "Robbed on the street",
            Money = LedgerTransaction.Transfer(c.CheckingAccount, _w.Accounts.External, amount, TransactionReason.CrimeProceeds, "Street robbery"),
        });

        private void Finish(StreetEncounter e, EncounterOutcome outcome, Money taken)
        {
            e.Outcome = outcome;
            _active.Remove(e.Character);
            var mugger = _w.Population.Get(e.Mugger);
            if (mugger != null && outcome != EncounterOutcome.Escaped || taken.Cents > 0)
                Report(e, mugger, taken);
            if (_w.Characters.TryGetValue(e.Character, out var c))
                _w.Phone.Send(c, EntityId.None, "", Phone.MessageCategory.Emergency,
                    outcome == EncounterOutcome.Complied ? "You handed over " + taken + ". The robbery has been reported." :
                    outcome == EncounterOutcome.Robbed ? "You were robbed of " + taken + "." :
                    outcome == EncounterOutcome.FoughtOff ? "You fought them off." : "You got away.");
            Ended?.Invoke(e);
        }

        /// <summary>The victim reports it: a crime with a named NPC suspect that the police can close with an arrest.</summary>
        private void Report(StreetEncounter e, NpcRecord mugger, Money taken)
        {
            if (mugger == null) return;
            var type = _w.Content.FindCrime("street_robbery");
            if (type == null) return;
            var incident = new CrimeIncident
            {
                Id = _w.Ids.Next(EntityKind.CrimeIncident), CrimeTypeId = type.Id, Perpetrator = mugger.Id, Victim = e.Character, Position = e.Position,
                District = _w.Crimes.DistrictAt(e.Position), OccurredAt = _w.Clock.Now, ReportedToPolice = true, ReportedAt = _w.Clock.Now, ValueStolenCents = taken.Cents,
            };
            _w.Justice.Incidents.Add(incident);
            _w.Dispatch.ReportCrime(incident, type);
            mugger.CriminalPropensity = Math.Min(1f, mugger.CriminalPropensity + 0.05f);
            _w.Dirty.Mark(SaveChunks.Justice);
        }

        /// <summary>A plausible local: an adult living in this district, the likeliest to try it.</summary>
        private NpcRecord PickMugger(WorldPosition at, DeterministicRandom rng)
        {
            var district = _w.Crimes.DistrictAt(at);
            var pool = district.IsValid ? _w.Census.AdultsOf(district) : _w.Census.Adults;
            NpcRecord best = null;
            var bestScore = -1.0;
            for (var i = 0; i < 40 && pool.Count > 0; i++)
            {
                var n = pool[rng.NextInt(0, pool.Count)];
                var age = n.AgeYears(_w.Today);
                if (!n.Alive || age < 16 || age > 50 || n.OverrideUntilDay > _w.Today) continue;
                var score = n.CriminalPropensity + rng.NextDouble() * 0.05;
                if (score > bestScore) { bestScore = score; best = n; }
            }
            return best;
        }
    }
}
