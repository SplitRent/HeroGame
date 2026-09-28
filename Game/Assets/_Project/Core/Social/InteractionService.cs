using System;
using System.Collections.Generic;

namespace HeroGame.Core.Social
{
    using HeroGame.Core.Characters;
    using HeroGame.Core.Economy;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Identity;
    using HeroGame.Core.Phone;
    using HeroGame.Core.Population;
    using HeroGame.Core.Weather;
    using HeroGame.Core.World;
    using SimWorld = HeroGame.Core.Simulation.World;

    public enum InteractionKind
    {
        Greet,
        SmallTalk,
        Compliment,
        Insult,
        Gift,
        Purchase,
        Favor,
        Threaten,
        Flirt,
        AskForNumber,
        AskAboutRumors,
    }

    public sealed class InteractionOutcome
    {
        public bool Accepted;
        public float AffinityDelta;
        public float TrustDelta;
        public float FearDelta;
        public string Line = "";
        public string LineId = "";
        public Familiarity FamiliarityAfter;
        public bool NumberShared;
        public string Rumor = "";
    }

    /// <summary>
    /// Player ↔ NPC interactions (GDD §92, §95–96). Each interaction nudges the NPC's summarised memory of
    /// the player (affinity, trust, fear, flags), is judged through the NPC's personality and current
    /// relationship, and produces a contextual line. Outcomes are deterministic per (NPC, player, minute).
    /// </summary>
    public sealed class InteractionService
    {
        private const int RecentLinesPerNpc = 6;

        private readonly SimWorld _world;
        private readonly BarkSelector _barks;
        private readonly Dictionary<EntityId, List<string>> _recentLines = new Dictionary<EntityId, List<string>>();

        public InteractionService(SimWorld world, IEnumerable<BarkLine> barks)
        {
            _world = world;
            _barks = new BarkSelector(barks);
        }

        public InteractionOutcome Interact(NpcRecord npc, ServerCharacter character, CharacterIdentity identity, InteractionKind kind, long giftCents = 0)
        {
            var now = _world.Clock.Now;
            var day = now.DayIndex;
            var memory = npc.MemoryOf(character.CharacterId, true, day);
            var p = npc.Personality;
            var rng = DeterministicRandom.For(_world.Seed, npc.Id.Value, character.CharacterId.Value, (ulong)(now.TotalSeconds / 60));
            var outcome = new InteractionOutcome { Accepted = true };
            var topic = "greet";

            // Diminishing returns: the tenth compliment today means less than the first.
            var sameDay = memory.LastSeenDay == day ? 1 : 0;
            var novelty = sameDay == 1 ? 0.5f : 1f;

            switch (kind)
            {
                case InteractionKind.Greet:
                    outcome.AffinityDelta = 0.01f * novelty;
                    break;
                case InteractionKind.SmallTalk:
                    outcome.Accepted = rng.NextDouble() < 0.45 + p.Extraversion * 0.4 + memory.Affinity * 0.2;
                    outcome.AffinityDelta = outcome.Accepted ? 0.04f * novelty : -0.01f;
                    topic = PickSmallTalkTopic(rng);
                    break;
                case InteractionKind.Compliment:
                    outcome.Accepted = rng.NextDouble() < 0.55 + p.Agreeableness * 0.3 - p.Neuroticism * 0.15 + memory.Affinity * 0.2;
                    outcome.AffinityDelta = outcome.Accepted ? 0.06f * novelty : -0.02f;
                    topic = outcome.Accepted ? "compliment_accepted" : "compliment_rejected";
                    break;
                case InteractionKind.Insult:
                    outcome.AffinityDelta = -0.15f - (1f - p.Agreeableness) * 0.1f;
                    outcome.TrustDelta = -0.05f;
                    memory.Flags |= MemoryFlags.HadConflict;
                    topic = "insulted";
                    break;
                case InteractionKind.Gift:
                    var income = Math.Max(1L, npc.AnnualSalaryCents / 365);
                    var generosity = Math.Min(1.0, giftCents / (double)income); // a day's pay feels generous
                    outcome.AffinityDelta = (float)(0.03 + 0.12 * generosity) * novelty;
                    outcome.TrustDelta = 0.02f;
                    memory.Flags |= MemoryFlags.ReceivedGift;
                    topic = "gift";
                    break;
                case InteractionKind.Purchase:
                    outcome.AffinityDelta = 0.015f * novelty;
                    memory.Flags |= MemoryFlags.Customer;
                    topic = "customer";
                    break;
                case InteractionKind.Favor:
                    outcome.AffinityDelta = 0.1f * novelty;
                    outcome.TrustDelta = 0.08f;
                    memory.Flags |= MemoryFlags.ReceivedFavor;
                    topic = "thanks";
                    break;
                case InteractionKind.Threaten:
                    outcome.FearDelta = 0.25f + p.Neuroticism * 0.2f;
                    outcome.AffinityDelta = -0.2f;
                    outcome.TrustDelta = -0.2f;
                    memory.Flags |= MemoryFlags.HadConflict;
                    topic = "threatened";
                    break;
                case InteractionKind.Flirt:
                    var single = npc.FindRelationship(RelationshipType.Spouse) == null && npc.FindRelationship(RelationshipType.Partner) == null;
                    var ageGap = Math.Abs(npc.AgeYears(day) - (identity?.Age ?? 25));
                    outcome.Accepted = npc.AgeYears(day) >= 18 && single && ageGap <= 15 &&
                                       rng.NextDouble() < 0.15 + memory.Affinity * 0.6 + p.Openness * 0.15;
                    outcome.AffinityDelta = outcome.Accepted ? 0.05f : -0.03f;
                    topic = outcome.Accepted ? "flirt_accepted" : "flirt_rejected";
                    break;
                case InteractionKind.AskForNumber:
                    outcome.Accepted = memory.Affinity >= 0.25f && memory.Interactions >= 3 && memory.Fear < 0.4f;
                    outcome.NumberShared = outcome.Accepted;
                    topic = outcome.Accepted ? "number_shared" : "number_refused";
                    break;
                case InteractionKind.AskAboutRumors:
                    outcome.Accepted = memory.Trust >= 0f && rng.NextDouble() < 0.4 + p.Extraversion * 0.4;
                    outcome.Rumor = outcome.Accepted ? Rumor(npc) : "";
                    topic = "gossip";
                    break;
            }

            // Fear makes everything a little colder; known criminals get a wary reception.
            memory.Affinity = Clamp(memory.Affinity + outcome.AffinityDelta, -1f, 1f);
            memory.Trust = Clamp(memory.Trust + outcome.TrustDelta, -1f, 1f);
            memory.Fear = Clamp(memory.Fear + outcome.FearDelta, 0f, 1f);
            memory.Interactions++;
            memory.LastSeenDay = day;
            memory.LastTopic = kind.ToString();
            outcome.FamiliarityAfter = BarkSelector.FamiliarityOf(memory);

            if (outcome.NumberShared && !HasContact(character, npc.Id))
                character.PhoneContacts.Add(new PhoneContact { Npc = npc.Id, Name = npc.FullName, AddedDay = day });

            // Neighbourhood reputation follows how you treat locals.
            var district = DistrictOf(npc);
            if (district.IsValid && outcome.AffinityDelta != 0f)
                character.Reputation.AddNeighborhood(district, outcome.AffinityDelta * 3f);

            var ctx = BuildContext(npc, character, identity, memory, topic);
            if (!string.IsNullOrEmpty(outcome.Rumor)) ctx.Tokens["rumor"] = outcome.Rumor;
            if (TrySpeak(npc, ctx, rng, out var line, out var lineId))
            {
                outcome.Line = line;
                outcome.LineId = lineId;
            }
            _world.Dirty.Mark(Simulation.SaveChunks.Population);
            _world.Dirty.Mark(Simulation.SaveChunks.CharacterPrefix + character.CharacterId);
            return outcome;
        }

        /// <summary>An unprompted line (ambient bark) when a player passes by. Does not change memory.</summary>
        public bool Ambient(NpcRecord npc, ServerCharacter character, CharacterIdentity identity, out string line)
        {
            var memory = npc.MemoryOf(character.CharacterId, false, 0);
            var ctx = BuildContext(npc, character, identity, memory, memory == null ? "idle" : "greet");
            var rng = DeterministicRandom.For(_world.Seed, npc.Id.Value, character.CharacterId.Value, (ulong)(_world.Clock.Now.TotalSeconds / 60) ^ 0xA3B);
            return TrySpeak(npc, ctx, rng, out line, out _);
        }

        /// <summary>Called by the crime system for every NPC who saw the player commit a crime.</summary>
        public void RecordWitnessedCrime(NpcRecord witness, EntityId character, int severity, bool victim)
        {
            var memory = witness.MemoryOf(character, true, _world.Today);
            memory.Flags |= victim ? MemoryFlags.VictimOfCrime | MemoryFlags.WitnessedCrime : MemoryFlags.WitnessedCrime;
            memory.Fear = Clamp(memory.Fear + 0.08f * severity * (0.5f + witness.Personality.Neuroticism), 0f, 1f);
            memory.Trust = Clamp(memory.Trust - 0.1f * severity, -1f, 1f);
            memory.Affinity = Clamp(memory.Affinity - 0.05f * severity * (victim ? 2f : 1f), -1f, 1f);
            memory.LastSeenDay = _world.Today;
            _world.Dirty.Mark(Simulation.SaveChunks.Population);
        }

        public BarkContext BuildContext(NpcRecord npc, ServerCharacter character, CharacterIdentity identity, CharacterMemory memory, string topic)
        {
            var now = _world.Clock.Now;
            var here = _world.Schedules.Resolve(npc, now);
            var place = _world.Geography.GetPlace(here.Place);
            var occ = _world.Occupations.Get(npc.OccupationId);
            var ctx = new BarkContext
            {
                Topic = topic,
                Familiarity = BarkSelector.FamiliarityOf(memory),
                Affinity = memory?.Affinity ?? 0f,
                Flags = memory?.Flags ?? MemoryFlags.None,
                DayPart = now.DayPart,
                Weather = _world.Weather.State.Current.Kind,
                OccupationCategory = occ != null ? occ.Category : npc.Employment.ToString(),
                PlaceKind = place != null ? place.Kind.ToString() : "",
            };
            foreach (var tag in WorldTags(character)) ctx.WorldTags.Add(tag);
            ctx.Tokens["npc_first"] = npc.FirstName;
            ctx.Tokens["player_first"] = identity != null ? identity.FirstName : "friend";
            ctx.Tokens["city"] = _world.Config.Identity.CityName;
            ctx.Tokens["place"] = place != null ? place.Name : "here";
            ctx.Tokens["weather"] = Describe(_world.Weather.State.Current.Kind);
            ctx.Tokens["temp"] = _world.Weather.State.Current.TemperatureC.ToString("0");
            ctx.Tokens["storm"] = _world.Weather.State.ActiveSystem != null ? _world.Weather.State.ActiveSystem.Name : "the storm";
            ctx.Tokens["team"] = _world.Config.Identity.SportsTeams.Count > 0 ? _world.Config.Identity.SportsTeams[0].Name : "the home team";
            ctx.Tokens["job"] = occ != null ? occ.Title.ToLowerInvariant() : "work";
            return ctx;
        }

        /// <summary>World-state tags that make NPC talk topical (all derived from live systems).</summary>
        public List<string> WorldTags(ServerCharacter character)
        {
            var tags = new List<string>();
            if (_world.Macro.InRecession) tags.Add("recession");
            if (_world.Weather.State.ActiveSystem != null) tags.Add("storm_coming");
            var w = _world.Weather.State.Current;
            if (w.TemperatureC >= 33f) tags.Add("heat");
            if (w.FloodLevel > 0.3f) tags.Add("flooding");
            if (_world.AnomalyLog.Count > 0 && _world.Today - _world.AnomalyLog[_world.AnomalyLog.Count - 1].OccurredAt.DayIndex <= 7) tags.Add("anomaly_recent");
            if (character != null)
            {
                var wanted = _world.Wanted.Get(character.CharacterId);
                if (wanted != null && wanted.Level > 0) tags.Add("player_wanted");
                if (character.Reputation.Get(ReputationDimension.Notoriety) >= 40f) tags.Add("player_notorious");
                if (character.Reputation.Get(ReputationDimension.Heroic) >= 40f) tags.Add("player_heroic");
            }
            return tags;
        }

        private bool TrySpeak(NpcRecord npc, BarkContext ctx, DeterministicRandom rng, out string line, out string lineId)
        {
            line = "";
            lineId = "";
            if (!_recentLines.TryGetValue(npc.Id, out var recent)) _recentLines[npc.Id] = recent = new List<string>();
            if (!_barks.TrySelect(ctx, rng, recent, out var result)) return false;
            line = result.Text;
            lineId = result.Line.Id;
            recent.Add(lineId);
            while (recent.Count > RecentLinesPerNpc) recent.RemoveAt(0);
            return true;
        }

        private string Rumor(NpcRecord npc)
        {
            // Real gossip from the server's history, preferring the NPC's own district.
            var recent = _world.History.Since(_world.Today - 10, 2);
            var district = DistrictOf(npc);
            foreach (var r in recent) if (r.District == district) return r.Headline;
            return recent.Count > 0 ? recent[recent.Count - 1].Headline : "";
        }

        private static string PickSmallTalkTopic(DeterministicRandom rng)
        {
            var topics = new[] { "weather", "work", "economy", "sports", "gossip" };
            return topics[rng.NextInt(0, topics.Length)];
        }

        private EntityId DistrictOf(NpcRecord npc)
        {
            var home = _world.Geography.GetPlace(npc.Home);
            return home != null ? home.District : EntityId.None;
        }

        private static bool HasContact(ServerCharacter c, EntityId npc)
        {
            foreach (var contact in c.PhoneContacts) if (contact.Npc == npc) return true;
            return false;
        }

        private static string Describe(WeatherKind kind)
        {
            switch (kind)
            {
                case WeatherKind.Clear: return "sunshine";
                case WeatherKind.PartlyCloudy: return "clouds";
                case WeatherKind.Overcast: return "grey sky";
                case WeatherKind.Fog: return "fog";
                case WeatherKind.LightRain: return "drizzle";
                case WeatherKind.HeavyRain: return "rain";
                case WeatherKind.Thunderstorm: return "storm";
                case WeatherKind.TropicalStorm: return "tropical storm";
                case WeatherKind.Hurricane: return "hurricane";
                default: return "weather";
            }
        }

        private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;
    }
}
