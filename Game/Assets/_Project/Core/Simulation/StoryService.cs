using System;
using System.Collections.Generic;
using System.Globalization;
using HeroGame.Core.Characters;
using HeroGame.Core.Economy;
using HeroGame.Core.Foundation;
using HeroGame.Core.Identity;
using HeroGame.Core.Population;
using HeroGame.Core.Powers;
using HeroGame.Core.Property;
using HeroGame.Core.Story;
using HeroGame.Core.Time;
using HeroGame.Core.Weather;
using HeroGame.Core.World;
using PhoneCategory = HeroGame.Core.Phone.MessageCategory;

namespace HeroGame.Core.Simulation
{
    /// <summary>
    /// Story Mode director (GDD §55–60). Runs authored missions on top of the same living world: cast members are
    /// real NPC records with schedules, objectives complete from what the player actually does (arriving, talking,
    /// working a shift, weathering a storm), dialogue choices set flags and relationships that later beats and the
    /// time jump read, and the four-year skip simulates those years for real. All authored text and scripting lives in
    /// story data; this class only interprets it.
    /// </summary>
    public sealed class StoryService
    {
        public const float ArrivalRadius = 30f;

        private readonly World _w;
        private readonly StoryDefinition _def;
        private readonly ServerCharacter _player;

        public readonly StoryState State;
        /// <summary>When set, time-changing effects run the simulation so the world catches up immediately.</summary>
        public WorldSimulation Simulation;

        public event Action<MissionDefinition> MissionStarted;
        public event Action<MissionDefinition> MissionCompleted;
        public event Action<ObjectiveDefinition> ObjectiveChanged;
        public event Action<DialogueNode> DialogueNodeShown;
        public event Action DialogueEnded;
        public event Action<CutsceneDefinition> CutsceneRequested;
        /// <summary>A mission with a checkpoint started: the runtime writes the checkpoint slot.</summary>
        public event Action<string> CheckpointRequested;

        private StoryService(World world, StoryDefinition definition, StoryState state, ServerCharacter player)
        {
            _w = world;
            _def = definition;
            State = state;
            _player = player;
            world.Story = state;
        }

        public StoryDefinition Definition => _def;
        public ServerCharacter Player => _player;

        /// <summary>Starts a new story in a freshly generated world: casts the characters and begins the first mission.</summary>
        public static StoryService Begin(World world, StoryDefinition definition, AccountProfile account)
        {
            var home = PickHome(world, definition.HomeStreet);
            account.Character.FirstName = definition.ProtagonistFirstName;
            account.Character.LastName = definition.ProtagonistLastName;
            var player = world.CreateCharacter(account, home != null ? home.Position : default);
            var state = new StoryState { StoryId = definition.Id, Home = home != null ? home.Id.ToString() : "" };
            state.Restrictions.AddRange(definition.TeenRestrictions);
            foreach (var m in definition.Missions) state.Missions.Add(new MissionState { Id = m.Id });
            var story = new StoryService(world, definition, state, player);
            story.CastCharacters(home);
            if (home != null && home.Property.IsValid) player.HomeProperty = home.Property;
            story.Unlock();
            return story;
        }

        /// <summary>Re-attaches to a loaded story (state from the save).</summary>
        public static StoryService Attach(World world, StoryDefinition definition, StoryState state, ServerCharacter player) =>
            new StoryService(world, definition, state, player);

        private static Place PickHome(World w, string street)
        {
            var occupied = new HashSet<EntityId>();
            foreach (var h in w.Population.Households) occupied.Add(h.Home);
            Place best = null;
            foreach (var p in w.Geography.PlacesOfKind(PlaceKind.Residence))
            {
                if (!string.IsNullOrEmpty(street) && p.Name.IndexOf(street, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (occupied.Contains(p.Id)) continue;
                if (best == null || p.Id.CompareTo(best.Id) < 0) best = p;
            }
            if (best != null) return best;
            foreach (var p in w.Geography.PlacesOfKind(PlaceKind.Residence))
                if (!string.IsNullOrEmpty(street) && p.Name.IndexOf(street, StringComparison.OrdinalIgnoreCase) >= 0) return p;
            return null;
        }

        // ------------------------------------------------------------------ cast

        private void CastCharacters(Place navarroHome)
        {
            var today = _w.Today;
            var residences = _w.Geography.PlacesOfKind(PlaceKind.Residence);
            residences.Sort((a, b) => a.Id.CompareTo(b.Id));
            Household navarros = null;
            var index = 0;
            foreach (var c in _def.Cast)
            {
                Place home;
                Household household;
                if (c.Home == "navarro" && navarroHome != null)
                {
                    if (navarros == null)
                    {
                        navarros = new Household { Id = _w.Ids.Next(EntityKind.Household), Surname = _def.ProtagonistLastName, Home = navarroHome.Id };
                        _w.Population.Add(navarros);
                    }
                    home = navarroHome;
                    household = navarros;
                }
                else
                {
                    home = !string.IsNullOrEmpty(c.Home) ? _w.Geography.FindPlaceByName(c.Home) : null;
                    if (home == null && residences.Count > 0) home = residences[(int)(StableHash.Of(c.Id) % (ulong)residences.Count)];
                    household = new Household { Id = _w.Ids.Next(EntityKind.Household), Surname = c.LastName, Home = home != null ? home.Id : EntityId.None };
                    _w.Population.Add(household);
                }
                var work = !string.IsNullOrEmpty(c.Workplace) ? _w.Geography.FindPlaceByName(c.Workplace) : null;
                var occupation = _w.Occupations.Get(c.Occupation);
                var npc = new NpcRecord
                {
                    Id = _w.Ids.Next(EntityKind.Npc),
                    FirstName = c.FirstName,
                    LastName = c.LastName,
                    BirthDay = today - c.Age * 365L - (long)(StableHash.Of(c.Id + "birth") % 300),
                    Sex = c.Sex == "Male" ? Sex.Male : Sex.Female,
                    AppearanceSeed = StableHash.Of("cast:" + c.Id),
                    Household = household.Id,
                    Home = home != null ? home.Id : EntityId.None,
                    IsImportant = true,
                    Education = c.Age >= 22 ? EducationLevel.Bachelors : c.Age >= 18 ? EducationLevel.HighSchool : EducationLevel.None,
                    Personality = new Personality { Openness = 0.6f, Conscientiousness = 0.6f, Extraversion = 0.5f, Agreeableness = 0.6f, Neuroticism = 0.4f },
                    LastSimulatedDay = today,
                };
                if (c.Age < 18)
                {
                    npc.Employment = c.Age >= 5 ? EmploymentStatus.Student : EmploymentStatus.Child;
                    npc.School = work != null ? work.Id : EntityId.None;
                }
                else if (occupation != null && work != null)
                {
                    npc.Employment = EmploymentStatus.Employed;
                    npc.OccupationId = occupation.Id;
                    npc.Workplace = work.Id;
                    npc.AnnualSalaryCents = occupation.MinSalaryCents + (occupation.MaxSalaryCents - occupation.MinSalaryCents) / 3;
                    npc.JobStartDay = today - 365 * 3;
                }
                else npc.Employment = EmploymentStatus.Unemployed;
                household.Members.Add(npc.Id);
                _w.Population.Add(npc);
                State.CastNpcs[c.Id] = npc.Id.ToString();
                index++;
            }
            _w.Dirty.Mark(SaveChunks.Population);
        }

        public NpcRecord Npc(string castId) =>
            State.CastNpcs.TryGetValue(castId, out var text) && EntityId.TryParse(text, out var id) ? _w.Population.Get(id) : null;

        public string CastIdOf(EntityId npc)
        {
            foreach (var kv in State.CastNpcs) if (kv.Value == npc.ToString()) return kv.Key;
            return null;
        }

        public string NameOf(string speaker)
        {
            if (speaker == "player") return _def.ProtagonistFirstName;
            if (speaker == "narrator" || string.IsNullOrEmpty(speaker)) return "";
            var c = _def.CastMember(speaker);
            return c != null ? c.FirstName : speaker;
        }

        // ------------------------------------------------------------------ queries

        public MissionState ActiveMission => State.Missions.Find(m => m.Status == MissionStatus.Active);

        public MissionDefinition ActiveDefinition
        {
            get
            {
                var a = ActiveMission;
                return a != null ? _def.Mission(a.Id) : null;
            }
        }

        public ObjectiveDefinition CurrentObjective
        {
            get
            {
                var a = ActiveMission;
                var d = a != null ? _def.Mission(a.Id) : null;
                return d != null && a.ObjectiveIndex < d.Objectives.Count ? d.Objectives[a.ObjectiveIndex] : null;
            }
        }

        public bool Allows(string action) => !State.Restrictions.Contains(action);

        public bool InDialogue => !string.IsNullOrEmpty(State.ActiveDialogue);

        public DialogueNode CurrentNode
        {
            get
            {
                var tree = _def.Dialogue(State.ActiveDialogue);
                return tree?.Node(State.ActiveNode);
            }
        }

        public List<DialogueChoice> AvailableChoices()
        {
            var list = new List<DialogueChoice>();
            var node = CurrentNode;
            if (node == null) return list;
            foreach (var c in node.Choices) if (AllTrue(c.Conditions)) list.Add(c);
            return list;
        }

        /// <summary>World position an objective points at (for the HUD marker and arrival checks).</summary>
        public bool TryObjectivePosition(ObjectiveDefinition o, out WorldPosition pos)
        {
            pos = default;
            if (o == null) return false;
            var where = !string.IsNullOrEmpty(o.Location) ? o.Location : o.Target;
            if (string.IsNullOrEmpty(where)) return false;
            if (where == "home")
            {
                if (!EntityId.TryParse(State.Home, out var h)) return false;
                var place = _w.Geography.GetPlace(h);
                if (place == null) return false;
                pos = place.Position;
                return true;
            }
            if (where.StartsWith("cast:", StringComparison.Ordinal) || o.Kind == ObjectiveKind.TalkTo && string.IsNullOrEmpty(o.Location))
            {
                var npc = Npc(where.StartsWith("cast:", StringComparison.Ordinal) ? where.Substring(5) : where);
                return npc != null && _w.Director.Index.TryGet(npc.Id, _w.Clock.Now, out _, out pos);
            }
            var named = _w.Geography.FindPlaceByName(where);
            if (named == null) return false;
            pos = named.Position;
            return true;
        }

        // ------------------------------------------------------------------ inputs

        /// <summary>Call regularly (every frame or tick) with the player's position.</summary>
        public void Update(WorldPosition playerPosition)
        {
            for (var guard = 0; guard < 16; guard++)
            {
                var o = CurrentObjective;
                var m = ActiveMission;
                if (o == null || m == null || InDialogue) return;
                if (!ObjectiveMet(o, m, playerPosition)) return;
                CompleteObjective();
            }
        }

        private bool ObjectiveMet(ObjectiveDefinition o, MissionState m, WorldPosition player)
        {
            var now = _w.Clock.Now.TotalSeconds;
            switch (o.Kind)
            {
                case ObjectiveKind.GoTo:
                    return TryObjectivePosition(o, out var at) && WorldPosition.DistanceXZ(at, player) <= ArrivalRadius;
                case ObjectiveKind.Wait:
                    if (TryParseClock(o.Target, out var minute))
                    {
                        var start = new GameDateTime(m.ObjectiveStartedSecond);
                        var target = start.StartOfDay.AddSeconds(minute * 60L);
                        if (target < start) target = target.AddDays(1);
                        return _w.Clock.Now >= target;
                    }
                    return now - m.ObjectiveStartedSecond >= o.Amount * GameDateTime.SecondsPerHour;
                case ObjectiveKind.Stay:
                    if (TryObjectivePosition(o, out var place) && WorldPosition.DistanceXZ(place, player) <= ArrivalRadius * 2f)
                    {
                        // Time counts only while present; leaving pauses the shift.
                        m.Progress += Math.Max(0, now - m.ObjectiveStartedSecond) / (float)GameDateTime.SecondsPerHour;
                    }
                    m.ObjectiveStartedSecond = now;
                    return m.Progress >= o.Amount;
                case ObjectiveKind.Earn:
                    return _w.Ledger.BalanceOf(_player.CheckingAccount).Cents >= (long)o.Amount;
                case ObjectiveKind.Condition:
                    return Check(o.Condition);
                case ObjectiveKind.Survive:
                    return StormPassed(m);
                default:
                    return false;
            }
        }

        private bool StormPassed(MissionState m)
        {
            var storm = _w.Weather.State.ActiveSystem;
            if (storm != null && _w.Clock.Now.HourIndex >= storm.LandfallHour) m.Progress = 1f;
            return m.Progress > 0f && (storm == null || _w.Clock.Now.HourIndex >= storm.LandfallHour + storm.DurationHours);
        }

        /// <summary>The player talked to an NPC. Returns true when the story took over the conversation.</summary>
        public bool TalkTo(EntityId npc)
        {
            var cast = CastIdOf(npc);
            return cast != null && TalkToCast(cast);
        }

        public bool TalkToCast(string castId)
        {
            var o = CurrentObjective;
            if (o == null || o.Kind != ObjectiveKind.TalkTo || InDialogue) return false;
            var target = o.Target.StartsWith("cast:", StringComparison.Ordinal) ? o.Target.Substring(5) : o.Target;
            if (target != castId) return false;
            if (string.IsNullOrEmpty(o.Dialogue)) CompleteObjective();
            else StartDialogue(o.Dialogue);
            return true;
        }

        public void Interact(string tag)
        {
            var o = CurrentObjective;
            if (o != null && o.Kind == ObjectiveKind.Interact && o.Target == tag && !InDialogue) CompleteObjective();
        }

        public void StartDialogue(string treeId)
        {
            var tree = _def.Dialogue(treeId);
            if (tree == null) return;
            State.ActiveDialogue = tree.Id;
            ShowNode(tree.Start);
        }

        private void ShowNode(string nodeId)
        {
            var tree = _def.Dialogue(State.ActiveDialogue);
            var node = tree?.Node(nodeId);
            if (node == null)
            {
                EndDialogue();
                return;
            }
            State.ActiveNode = node.Id;
            ApplyAll(node.Effects);
            DialogueNodeShown?.Invoke(node);
        }

        /// <summary>Advance a node that has no choices.</summary>
        public void Continue()
        {
            var node = CurrentNode;
            if (node == null) return;
            if (AvailableChoices().Count > 0) return;
            if (string.IsNullOrEmpty(node.Next)) EndDialogue();
            else ShowNode(node.Next);
        }

        public void Choose(int index)
        {
            var choices = AvailableChoices();
            if (index < 0 || index >= choices.Count) return;
            var choice = choices[index];
            State.Log.Add(State.ActiveDialogue + ": " + choice.Text);
            ApplyAll(choice.Effects);
            if (string.IsNullOrEmpty(choice.Next)) EndDialogue();
            else ShowNode(choice.Next);
        }

        private void EndDialogue()
        {
            var wasObjectiveDialogue = CurrentObjective != null && CurrentObjective.Dialogue == State.ActiveDialogue;
            State.ActiveDialogue = "";
            State.ActiveNode = "";
            DialogueEnded?.Invoke();
            if (wasObjectiveDialogue) CompleteObjective();
        }

        // ------------------------------------------------------------------ progression

        private void Unlock()
        {
            foreach (var d in _def.Missions)
            {
                var s = State.Mission(d.Id);
                if (s.Status != MissionStatus.Locked) continue;
                var ready = true;
                foreach (var r in d.Requires)
                {
                    var req = State.Mission(r);
                    if (req == null || req.Status != MissionStatus.Completed) ready = false;
                }
                if (!ready || d.Part > State.Part) continue;
                s.Status = MissionStatus.Available;
            }
            if (ActiveMission != null) return;
            foreach (var d in _def.Missions)
            {
                var s = State.Mission(d.Id);
                if (s.Status == MissionStatus.Available && d.AutoStart)
                {
                    StartMission(d.Id);
                    return;
                }
            }
        }

        public void StartMission(string id)
        {
            var d = _def.Mission(id);
            var s = State.Mission(id);
            if (d == null || s == null || s.Status == MissionStatus.Active || s.Status == MissionStatus.Completed) return;
            var current = ActiveMission;
            if (current != null && current.Id != id) return;
            s.Status = MissionStatus.Active;
            s.StartedDay = _w.Today;
            s.ObjectiveIndex = 0;
            s.Progress = 0f;
            s.ObjectiveStartedSecond = _w.Clock.Now.TotalSeconds;
            if (d.Checkpoint) CheckpointRequested?.Invoke(d.Id);
            MissionStarted?.Invoke(d);
            ApplyAll(d.OnStart);
            SkipInapplicable();
            ObjectiveChanged?.Invoke(CurrentObjective);
        }

        private void CompleteObjective()
        {
            var s = ActiveMission;
            var d = s != null ? _def.Mission(s.Id) : null;
            if (d == null) return;
            var o = d.Objectives[s.ObjectiveIndex];
            s.ObjectiveIndex++;
            s.Progress = 0f;
            s.ObjectiveStartedSecond = _w.Clock.Now.TotalSeconds;
            ApplyAll(o.OnComplete);
            if (ActiveMission != s) return; // an effect ended or replaced the mission
            SkipInapplicable();
            if (s.ObjectiveIndex >= d.Objectives.Count) CompleteMission(d, s);
            else ObjectiveChanged?.Invoke(CurrentObjective);
        }

        /// <summary>Branching: objectives whose condition is false are skipped (e.g. only if you took the delivery).</summary>
        private void SkipInapplicable()
        {
            var s = ActiveMission;
            var d = s != null ? _def.Mission(s.Id) : null;
            while (d != null && s.ObjectiveIndex < d.Objectives.Count)
            {
                var o = d.Objectives[s.ObjectiveIndex];
                if (o.Kind == ObjectiveKind.Condition || string.IsNullOrEmpty(o.Condition) || Check(o.Condition)) break;
                s.ObjectiveIndex++;
            }
        }

        private void CompleteMission(MissionDefinition d, MissionState s)
        {
            s.Status = MissionStatus.Completed;
            s.CompletedDay = _w.Today;
            ApplyAll(d.OnComplete);
            MissionCompleted?.Invoke(d);
            _w.Dirty.Mark(SaveChunks.Story);
            Unlock();
        }

        // ------------------------------------------------------------------ effects & conditions

        public bool AllTrue(List<string> conditions)
        {
            foreach (var c in conditions) if (!Check(c)) return false;
            return true;
        }

        public bool Check(string condition)
        {
            if (string.IsNullOrEmpty(condition)) return true;
            var op = StoryOp.Parse(condition);
            switch (op.Verb)
            {
                case "flag":
                {
                    var (name, cmp) = StoryOp.SplitComparison(op.Arg(0));
                    return StoryOp.Compare(State.Flag(name), cmp, out _);
                }
                case "rel":
                {
                    var (name, cmp) = StoryOp.SplitComparison(op.Arg(0));
                    return StoryOp.Compare(State.Relationship(name), cmp, out _);
                }
                case "money": return StoryOp.Compare(_w.Ledger.BalanceOf(_player.CheckingAccount).Cents, op.Arg(0), out _);
                case "hour": return StoryOp.Compare(_w.Clock.Now.Hour, op.Arg(0), out _);
                case "part": return StoryOp.Compare(State.Part, op.Arg(0), out _);
                case "item": return _player.Inventory.Exists(s => s.ItemId == op.Arg(0));
                case "mission":
                {
                    var (name, cmp) = StoryOp.SplitComparison(op.Arg(0));
                    var m = State.Mission(name);
                    return m != null && cmp.Length > 1 && m.Status.ToString() == cmp.Substring(1);
                }
                case "power":
                {
                    // power:stage>=1 (0 Latent, 1 Manifesting, 2 Aware…) or power:uses>=3 (successful deliberate uses).
                    var (what, cmp) = StoryOp.SplitComparison(op.Arg(0));
                    double value = 0;
                    foreach (var p in _player.Powers.Powers)
                        value = what == "uses" ? value + p.Progress.SuccessfulUses : Math.Max(value, (int)p.Stage);
                    return StoryOp.Compare(value, cmp, out _);
                }
                case "rep":
                {
                    var (dim, cmp) = StoryOp.SplitComparison(op.Arg(0));
                    return Enum.TryParse(dim, out ReputationDimension d) && StoryOp.Compare(_player.Reputation.Get(d), cmp, out _);
                }
                case "ordinance":
                {
                    var (id, cmp) = StoryOp.SplitComparison(op.Arg(0));
                    return StoryOp.Compare(_w.Civic.IsActive(id) ? 1 : 0, cmp, out _);
                }
                case "registered": return StoryOp.Compare(_w.Government.IsRegistered(_player.CharacterId) ? 1 : 0, op.Arg(0), out _);
                case "wanted":
                {
                    var status = _w.Wanted.Get(_player.CharacterId);
                    return StoryOp.Compare(status != null ? status.Level : 0, op.Arg(0), out _);
                }
                default: return false;
            }
        }

        private void ApplyAll(List<string> effects)
        {
            foreach (var e in effects) Apply(e);
            _w.Dirty.Mark(SaveChunks.Story);
        }

        public void Apply(string effect)
        {
            var op = StoryOp.Parse(effect);
            switch (op.Verb)
            {
                case "flag": ApplyNumber(op.Arg(0), State.Flags); break;
                case "rel":
                {
                    var (name, delta) = SplitDelta(op.Arg(0));
                    State.Relationships[name] = Math.Max(-1f, Math.Min(1f, State.Relationship(name) + (float)delta));
                    var npc = Npc(name);
                    if (npc != null)
                    {
                        var memory = npc.MemoryOf(_player.CharacterId, true, _w.Today);
                        memory.Affinity = Math.Max(-1f, Math.Min(1f, memory.Affinity + (float)delta));
                    }
                    break;
                }
                case "money": Money(long.Parse(op.Arg(0), CultureInfo.InvariantCulture), "Story"); break;
                case "pay": PayFrom(op.Arg(0), long.Parse(op.Arg(1), CultureInfo.InvariantCulture)); break;
                case "mission":
                    switch (op.Arg(0))
                    {
                        case "start": StartMission(op.Arg(1)); break;
                        case "complete":
                            var s = State.Mission(op.Arg(1));
                            var d = _def.Mission(op.Arg(1));
                            if (s != null && d != null && s.Status == MissionStatus.Active) CompleteMission(d, s);
                            break;
                        case "fail":
                            var f = State.Mission(op.Arg(1));
                            if (f != null) f.Status = MissionStatus.Failed;
                            break;
                    }
                    break;
                case "time":
                    if (op.Arg(0) == "advance") Advance((long)(double.Parse(op.Arg(1), CultureInfo.InvariantCulture) * GameDateTime.SecondsPerHour));
                    else if (op.Arg(0) == "to" && TryParseClock(op.Arg(1) + ":" + op.Arg(2), out var minute))
                    {
                        var now = _w.Clock.Now;
                        var target = now.StartOfDay.AddSeconds(minute * 60L);
                        if (target <= now) target = target.AddDays(1);
                        Advance(target.TotalSeconds - now.TotalSeconds);
                    }
                    break;
                case "storm": Storm(op.Arg(0), float.Parse(op.Arg(1), CultureInfo.InvariantCulture), int.Parse(op.Arg(2), CultureInfo.InvariantCulture)); break;
                case "anomaly": Expose(op); break;
                case "cutscene":
                    var cut = _def.Cutscene(op.Arg(0));
                    if (cut != null) CutsceneRequested?.Invoke(cut);
                    break;
                case "timejump": TimeJump(_def.TimeJump(op.Arg(0))); break;
                case "part":
                    State.Part = int.Parse(op.Arg(0), CultureInfo.InvariantCulture);
                    if (State.Part >= 2) State.Restrictions.Clear();
                    Unlock();
                    break;
                case "history":
                    _w.History.Record(_w.Today, HistoryCategory.People, int.Parse(op.Arg(0), CultureInfo.InvariantCulture), Rest(op, 1));
                    break;
                case "item":
                    if (op.Arg(0) == "give") _player.Inventory.Add(new InventoryStack { ItemId = op.Arg(1) });
                    else _player.Inventory.RemoveAll(s => s.ItemId == op.Arg(1));
                    break;
                case "restrict":
                    if (op.Arg(0) == "add" && !State.Restrictions.Contains(op.Arg(1))) State.Restrictions.Add(op.Arg(1));
                    else State.Restrictions.Remove(op.Arg(1));
                    break;
                case "cast": Cast(op); break;
                case "say":
                    _w.Phone.Send(_player, EntityId.None, NameOf(op.Arg(0)), PhoneCategory.Personal, Rest(op, 1));
                    break;
                case "ordinance": Ordinance(op); break;
                case "election": Election(op); break;
                case "disaster":
                {
                    var place = _w.Geography.FindPlaceByName(Rest(op, 1));
                    var district = place != null ? _w.Geography.GetDistrict(place.District) : null;
                    if (Enum.TryParse(op.Arg(0), out Civic.DisasterKind kind)) _w.Calendar.Trigger(kind, district, _w.Clock.Now);
                    break;
                }
                case "fire":
                {
                    var place = _w.Geography.FindPlaceByName(Rest(op, 0));
                    var property = place != null ? _w.Properties.Get(place.Property) : null;
                    if (property != null) _w.Dispatch.ReportFire(property, 0.35f, "story");
                    break;
                }
                case "register": _w.Government.RegisterPowers(_player); break;
                case "opinion":
                {
                    var delta = float.Parse(op.Arg(1), CultureInfo.InvariantCulture);
                    foreach (var o in _w.Civic.Opinion) o.Support[op.Arg(0)] = Math.Max(-1f, Math.Min(1f, o.Of(op.Arg(0)) + delta));
                    _w.Dirty.Mark(SaveChunks.Civic);
                    break;
                }
                case "rep":
                    if (Enum.TryParse(op.Arg(0), out ReputationDimension dim)) _player.Reputation.Add(dim, float.Parse(op.Arg(1), CultureInfo.InvariantCulture));
                    break;
                case "transfer":
                    Transfer(new PropertyTransfer { District = op.Arg(0), Share = float.Parse(op.Arg(1), CultureInfo.InvariantCulture), NewOwner = op.Arg(2), FromOwner = op.Arg(3) });
                    break;
                case "ending":
                    State.Ending = op.Arg(0);
                    State.Flags["ending_" + op.Arg(0)] = 1;
                    _w.History.Record(_w.Today, HistoryCategory.People, 5, Rest(op, 1).Length > 0 ? Rest(op, 1) : "A chapter of " + _w.Config.Identity.CityName + "'s story closes");
                    break;
            }
        }

        /// <summary>ordinance:ID:propose|vote|enact|repeal — "vote" makes the council decide a pending proposal today.</summary>
        private void Ordinance(StoryOp op)
        {
            var gov = _w.Government;
            var def = gov.Ordinance(op.Arg(0));
            if (def == null) return;
            switch (op.Arg(1))
            {
                case "propose": gov.Propose(EntityId.None, def.Id, _w.Civic.IsActive(def.Id)); break;
                case "vote":
                    foreach (var p in _w.Civic.Proposals) if (!p.Decided && p.OrdinanceId == def.Id) p.VoteDay = _w.Today;
                    gov.DecideNow(_w.Today);
                    break;
                case "enact": gov.Enact(def); break;
                case "repeal": gov.Repeal(def); break;
            }
        }

        /// <summary>
        /// election:schedule · election:candidate:CAST:SLATE (a cast member runs for mayor) · election:boost:CAST:AMOUNT
        /// (campaign recognition) · election:hold (the city votes now; sets flag mayor_CAST for the winner if cast).
        /// </summary>
        private void Election(StoryOp op)
        {
            var gov = _w.Government;
            Civic.Election Mayor() => _w.Civic.Elections.Find(e => e.Office == Civic.Office.Mayor && !e.Held);
            switch (op.Arg(0))
            {
                case "schedule":
                    if (Mayor() == null) gov.ScheduleElections(_w.Today);
                    break;
                case "candidate":
                {
                    var e = Mayor();
                    var npc = Npc(op.Arg(1));
                    if (e == null || npc == null || e.Candidates.Exists(c => c.Person == npc.Id)) break;
                    e.Candidates.Add(new Civic.Candidate { Person = npc.Id, Name = npc.FullName, Slate = op.Arg(2), Recognition = 0.35f });
                    break;
                }
                case "boost":
                {
                    var e = Mayor();
                    var npc = Npc(op.Arg(1));
                    var c = e != null && npc != null ? e.Candidates.Find(x => x.Person == npc.Id) : null;
                    if (c != null) c.Recognition = Math.Min(0.95f, c.Recognition + float.Parse(op.Arg(2), CultureInfo.InvariantCulture));
                    break;
                }
                case "hold":
                {
                    var e = Mayor();
                    if (e == null) break;
                    for (var d = _w.Today; !e.Held && d <= e.ElectionDay + 1; d++) gov.ProcessDay(Math.Max(d, e.ElectionDay));
                    var cast = CastIdOf(e.Winner);
                    if (cast != null) State.Flags["mayor_" + cast] = 1;
                    break;
                }
            }
            _w.Dirty.Mark(SaveChunks.Civic);
        }

        private static string Rest(StoryOp op, int from) => string.Join(":", op.Args, from, Math.Max(0, op.Args.Length - from));

        private static (string name, double delta) SplitDelta(string s)
        {
            for (var i = 1; i < s.Length; i++)
                if (s[i] == '+' || s[i] == '-' || s[i] == '=')
                    return (s.Substring(0, i), double.Parse(s.Substring(s[i] == '=' ? i + 1 : i), CultureInfo.InvariantCulture));
            return (s, 0);
        }

        private static void ApplyNumber(string expr, Dictionary<string, int> into)
        {
            var eq = expr.IndexOf('=');
            if (eq > 0)
            {
                into[expr.Substring(0, eq)] = int.Parse(expr.Substring(eq + 1), CultureInfo.InvariantCulture);
                return;
            }
            var (name, delta) = SplitDelta(expr);
            into.TryGetValue(name, out var v);
            into[name] = v + (int)delta;
        }

        private void Money(long cents, string memo)
        {
            if (cents == 0) return;
            var tx = cents > 0
                ? LedgerTransaction.Transfer(_w.Accounts.External, _player.CheckingAccount, new Money(cents), TransactionReason.Wage, memo)
                : LedgerTransaction.Transfer(_player.CheckingAccount, _w.Accounts.External, new Money(-cents), TransactionReason.Purchase, memo);
            _w.Transactions.Execute(new WorldTransaction { Source = TransactionSource.Simulation, Timestamp = _w.Clock.Now, Description = memo, Money = tx });
        }

        /// <summary>Wages from a real business in the world (Lupe pays from her till).</summary>
        private void PayFrom(string placeName, long cents)
        {
            var place = _w.Geography.FindPlaceByName(placeName);
            foreach (var b in _w.Businesses.Values)
            {
                if (place == null || b.Place != place.Id) continue;
                var paid = _w.Transactions.Execute(new WorldTransaction
                {
                    Source = TransactionSource.Simulation,
                    Timestamp = _w.Clock.Now,
                    Description = "Wages from " + b.Name,
                    Money = LedgerTransaction.Transfer(b.Account, _player.CheckingAccount, new Money(cents), TransactionReason.Payroll, "Wages"),
                }).Success;
                if (paid) return;
            }
            Money(cents, "Wages");
        }

        private void Advance(long seconds)
        {
            if (seconds <= 0) return;
            _w.Clock.AdvanceGame(seconds);
            Simulation?.Update();
        }

        private void Storm(string name, float category, int hoursToLandfall)
        {
            var hour = _w.Clock.Now.HourIndex;
            _w.Weather.State.ActiveSystem = new TropicalSystem
            {
                Name = name, AnnouncedHour = hour, LandfallHour = hour + hoursToLandfall, DurationHours = 12, Category = category,
            };
        }

        /// <summary>anomaly:player:DAYS:DOMAINS:ELEMENTS:INTENSITY — the protagonist is exposed; the ability stays latent for DAYS.</summary>
        private void Expose(StoryOp op)
        {
            var days = long.Parse(op.Arg(1), CultureInfo.InvariantCulture);
            var signature = new AnomalySignature { Intensity = float.Parse(op.Arg(4), CultureInfo.InvariantCulture) };
            foreach (var d in op.Arg(2).Split(','))
                if (Enum.TryParse(d, out PowerDomain domain)) signature.Domains.Add(new DomainAffinity { Domain = domain, Weight = 1f });
            foreach (var e in op.Arg(3).Split(','))
                if (Enum.TryParse(e, out PowerElement element)) signature.Elements.Add(element);
            // Story Mode fixes the ability family per playthrough seed (GDD §58).
            var rng = DeterministicRandom.For(_w.Seed, _player.CharacterId.Value, 0x57041);
            var definition = _w.PowerGenerator.Generate(signature, rng);
            if (definition == null) return;
            _player.Powers.Exposures++;
            _player.Powers.Powers.Add(new PowerInstance
            {
                Definition = definition, Stage = PowerStage.Latent, AcquiredDay = _w.Today, StageChangedDay = _w.Today, ManifestDay = _w.Today + days,
            });
            _w.Dirty.Mark(SaveChunks.CharacterPrefix + _player.CharacterId);
        }

        private void Cast(StoryOp op)
        {
            var npc = Npc(op.Arg(0));
            if (npc == null) return;
            switch (op.Arg(1))
            {
                case "arrest":
                    npc.Arrests++;
                    npc.HasCriminalRecord = true;
                    npc.AddHistory(_w.Today, "arrested", npc.FullName + " was arrested.");
                    break;
                case "job":
                    var place = _w.Geography.FindPlaceByName(Rest(op, 3));
                    var occ = _w.Occupations.Get(op.Arg(2));
                    if (occ != null)
                    {
                        npc.OccupationId = occ.Id;
                        npc.Employment = EmploymentStatus.Employed;
                        npc.Workplace = place != null ? place.Id : EntityId.None;
                        npc.JobStartDay = _w.Today;
                        _w.Population.Reindex(npc);
                        _w.Director.Invalidate(npc.Id);
                    }
                    break;
            }
            _w.Dirty.Mark(SaveChunks.Population);
        }

        // ------------------------------------------------------------------ time jump

        /// <summary>
        /// FOUR YEARS LATER (GDD §57): the world simulates the years (people age, move, change jobs, are born and die;
        /// businesses rise and fail; storms come), then the authored canon changes apply on top.
        /// </summary>
        public void TimeJump(TimeJumpDefinition jump)
        {
            if (jump == null) return;
            var sim = Simulation ?? new WorldSimulation(_w);
            var days = jump.Years * 365;
            while (days > 0)
            {
                var step = Math.Min(30, days);
                _w.Clock.AdvanceGame(step * GameDateTime.SecondsPerDay);
                sim.Update();
                days -= step;
            }
            foreach (var change in jump.CastChanges)
            {
                if (!string.IsNullOrEmpty(change.Condition) && !Check(change.Condition)) continue;
                var npc = Npc(change.Cast);
                if (npc == null) continue;
                if (!string.IsNullOrEmpty(change.Occupation)) Apply("cast:" + change.Cast + ":job:" + change.Occupation + ":" + change.Workplace);
                if (!string.IsNullOrEmpty(change.Note)) npc.AddHistory(_w.Today, "story", change.Note);
            }
            foreach (var t in jump.PropertyTransfers) Transfer(t);
            foreach (var h in jump.Headlines) _w.History.Record(_w.Today, HistoryCategory.Economy, 4, h);
            ApplyAll(jump.Effects);
            if (EntityId.TryParse(State.Home, out var home) && _w.Geography.GetPlace(home) is Place p) _player.LastPosition = p.Position;
            var cut = _def.Cutscene(jump.Cutscene);
            if (cut != null) CutsceneRequested?.Invoke(cut);
        }

        /// <summary>A developer buys a share of a district's homes at market value (the Meridian land grab).</summary>
        private void Transfer(PropertyTransfer t)
        {
            District district = null;
            foreach (var d in _w.Geography.Districts) if (d.Key == t.District) district = d;
            if (district == null) return;
            // The same organisation keeps its identity across events (Meridian in the jump and in the ending).
            var buyer = OrganizationNamed(t.NewOwner);
            if (!buyer.IsValid)
            {
                buyer = _w.Ids.Next(EntityKind.Organization);
                _w.Ledger.Open(_w.Ids.Next(EntityKind.LedgerAccount), buyer, LedgerAccountKind.Business, t.NewOwner);
            }
            var from = string.IsNullOrEmpty(t.FromOwner) ? EntityId.None : OrganizationNamed(t.FromOwner);
            if (!string.IsNullOrEmpty(t.FromOwner) && !from.IsValid) return;
            var homes = new List<PropertyRecord>();
            foreach (var p in _w.Properties.All)
                if (p.District == district.Id && (p.Kind == PropertyKind.House || p.Kind == PropertyKind.Land) && _w.Ownership.OwnerOf(p.Id) is EntityId o && !_w.Characters.ContainsKey(o)
                    && o != buyer && (!from.IsValid || o == from))
                    homes.Add(p);
            homes.Sort((a, b) => a.Id.CompareTo(b.Id));
            var take = (int)Math.Round(homes.Count * t.Share);
            for (var i = 0; i < take; i++)
            {
                var p = homes[i];
                var seller = _w.Ownership.OwnerOf(p.Id);
                var price = new Money(p.MarketValueCents);
                var money = new LedgerTransaction { Reason = TransactionReason.Purchase, Memo = t.NewOwner + " acquisition" };
                money.Add(_w.Accounts.External, -price.Cents);
                money.Add(seller.IsValid ? _w.AccountFor(seller) : _w.Accounts.Treasury, price.Cents);
                var tx = new WorldTransaction { Source = TransactionSource.Simulation, Timestamp = _w.Clock.Now, Description = t.NewOwner + " buys " + p.Address, Money = money };
                tx.Ownership.Add(new OwnershipChange { Asset = p.Id, From = seller, To = buyer });
                if (_w.Transactions.Execute(tx).Success) p.ForSale = false;
            }
        }

        private EntityId OrganizationNamed(string name)
        {
            foreach (var a in _w.Ledger.Accounts)
                if (a.Kind == LedgerAccountKind.Business && a.Label == name) return a.Owner;
            return EntityId.None;
        }

        private static bool TryParseClock(string text, out int minute)
        {
            minute = 0;
            if (string.IsNullOrEmpty(text) || text.IndexOf(':') < 0) return false;
            var parts = text.Split(':');
            if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
            minute = h * 60 + m;
            return true;
        }
    }
}
