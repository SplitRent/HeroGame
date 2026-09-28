using System;
using System.Collections.Generic;
using System.Globalization;

namespace HeroGame.Core.Story
{
    /// <summary>An authored character (GDD §5): injected into the story world as a persistent, protected NPC.</summary>
    [Serializable]
    public sealed class CastMember
    {
        public string Id = "";
        public string FirstName = "";
        public string LastName = "";
        public int Age;
        public string Sex = "Female";
        public string Occupation = "";
        /// <summary>Place name where they work or study ("" = none).</summary>
        public string Workplace = "";
        /// <summary>"navarro" = the protagonist's home; otherwise a place name; "" = any Eastwater residence.</summary>
        public string Home = "";
        public string Role = "";
    }

    public enum ObjectiveKind
    {
        /// <summary>Reach a place (<see cref="ObjectiveDefinition.Target"/>: place name, "home" or "cast:ID").</summary>
        GoTo,
        /// <summary>Talk to a cast member; starts <see cref="ObjectiveDefinition.Dialogue"/> if set.</summary>
        TalkTo,
        /// <summary>Wait until a time of day ("HH:MM") or for a number of hours.</summary>
        Wait,
        /// <summary>Spend <see cref="ObjectiveDefinition.Amount"/> game hours at a place (a work shift, a class).</summary>
        Stay,
        /// <summary>Hold at least Amount cents in the checking account.</summary>
        Earn,
        /// <summary>Use a tagged world interactable ("sandbags", "delivery_drop"…).</summary>
        Interact,
        /// <summary>Completes when <see cref="ObjectiveDefinition.Condition"/> becomes true.</summary>
        Condition,
        /// <summary>Wait out the current storm.</summary>
        Survive,
    }

    [Serializable]
    public sealed class ObjectiveDefinition
    {
        public string Id = "";
        public string Text = "";
        public ObjectiveKind Kind;
        public string Target = "";
        /// <summary>Where an Interact/Stay objective happens when Target is a tag: place name, "home" or "cast:ID".</summary>
        public string Location = "";
        public float Amount;
        public string Dialogue = "";
        public string Condition = "";
        public bool Optional;
        /// <summary>Effects applied when the objective completes.</summary>
        public List<string> OnComplete = new List<string>();
    }

    [Serializable]
    public sealed class MissionDefinition
    {
        public string Id = "";
        public string Title = "";
        public string Summary = "";
        public int Part = 1;
        public List<string> Requires = new List<string>();
        /// <summary>Starts itself as soon as it becomes available (the next beat of a linear chapter).</summary>
        public bool AutoStart = true;
        public List<string> OnStart = new List<string>();
        public List<ObjectiveDefinition> Objectives = new List<ObjectiveDefinition>();
        public List<string> OnComplete = new List<string>();
        /// <summary>Write a checkpoint save when this mission starts.</summary>
        public bool Checkpoint = true;
    }

    [Serializable]
    public sealed class DialogueChoice
    {
        public string Text = "";
        public string Next = "";
        /// <summary>Shown only when every condition holds.</summary>
        public List<string> Conditions = new List<string>();
        public List<string> Effects = new List<string>();
    }

    [Serializable]
    public sealed class DialogueNode
    {
        public string Id = "";
        /// <summary>Cast id, "player" or "narrator".</summary>
        public string Speaker = "";
        public string Text = "";
        public List<string> Effects = new List<string>();
        public List<DialogueChoice> Choices = new List<DialogueChoice>();
        /// <summary>Next node when there are no choices ("" ends the conversation).</summary>
        public string Next = "";
    }

    [Serializable]
    public sealed class DialogueTree
    {
        public string Id = "";
        public string Start = "";
        public List<DialogueNode> Nodes = new List<DialogueNode>();

        public DialogueNode Node(string id)
        {
            foreach (var n in Nodes) if (n.Id == id) return n;
            return null;
        }
    }

    [Serializable]
    public sealed class CutsceneShot
    {
        /// <summary>"player", "cast:ID", "place:NAME" or "sky".</summary>
        public string Camera = "player";
        public string Speaker = "";
        public string Text = "";
        public float Seconds = 3f;
    }

    [Serializable]
    public sealed class CutsceneDefinition
    {
        public string Id = "";
        public string Title = "";
        public List<CutsceneShot> Shots = new List<CutsceneShot>();
    }

    [Serializable]
    public sealed class CastChange
    {
        public string Cast = "";
        public string Occupation = "";
        public string Workplace = "";
        /// <summary>Only applies when this condition holds (e.g. "flag:rafa_arrested>=1").</summary>
        public string Condition = "";
        public string Note = "";
    }

    [Serializable]
    public sealed class PropertyTransfer
    {
        public string District = "";
        /// <summary>Fraction of the district's residential properties that change hands.</summary>
        public float Share;
        public string NewOwner = "";
        /// <summary>Only properties currently owned by this organisation change hands (empty: any non-player owner).</summary>
        public string FromOwner = "";
    }

    /// <summary>The four-year skip between Part One and Part Two (GDD §57): the world genuinely lives those years.</summary>
    [Serializable]
    public sealed class TimeJumpDefinition
    {
        public string Id = "";
        public int Years = 4;
        public string Cutscene = "";
        public List<CastChange> CastChanges = new List<CastChange>();
        public List<PropertyTransfer> PropertyTransfers = new List<PropertyTransfer>();
        public List<string> Headlines = new List<string>();
        public List<string> Effects = new List<string>();
    }

    [Serializable]
    public sealed class StoryDefinition
    {
        public string Id = "";
        public string Title = "";
        /// <summary>Calendar start "YYYY-MM-DD HH:MM".</summary>
        public string Start = "2026-08-17 07:00";
        public string ProtagonistFirstName = "";
        public string ProtagonistLastName = "";
        public int ProtagonistAge = 16;
        /// <summary>Street for the protagonist's home.</summary>
        public string HomeStreet = "";
        /// <summary>Player actions unavailable during Part One (a sixteen-year-old cannot buy a house).</summary>
        public List<string> TeenRestrictions = new List<string>();
        public List<CastMember> Cast = new List<CastMember>();
        public List<MissionDefinition> Missions = new List<MissionDefinition>();
        public List<DialogueTree> Dialogues = new List<DialogueTree>();
        public List<CutsceneDefinition> Cutscenes = new List<CutsceneDefinition>();
        public List<TimeJumpDefinition> TimeJumps = new List<TimeJumpDefinition>();

        public MissionDefinition Mission(string id) => Missions.Find(m => m.Id == id);
        public DialogueTree Dialogue(string id) => Dialogues.Find(d => d.Id == id);
        public CutsceneDefinition Cutscene(string id) => Cutscenes.Find(c => c.Id == id);
        public CastMember CastMember(string id) => Cast.Find(c => c.Id == id);
        public TimeJumpDefinition TimeJump(string id) => TimeJumps.Find(t => t.Id == id);
    }

    public enum MissionStatus
    {
        Locked,
        Available,
        Active,
        Completed,
        Failed,
    }

    [Serializable]
    public sealed class MissionState
    {
        public string Id = "";
        public MissionStatus Status;
        public int ObjectiveIndex;
        /// <summary>Progress within the current objective (hours stayed, etc.).</summary>
        public float Progress;
        /// <summary>Game second the current objective began (Wait/Stay).</summary>
        public long ObjectiveStartedSecond;
        public long StartedDay;
        public long CompletedDay;
    }

    [Serializable]
    public sealed class StoryState
    {
        public string StoryId = "";
        public int Part = 1;
        public List<MissionState> Missions = new List<MissionState>();
        public Dictionary<string, int> Flags = new Dictionary<string, int>();
        public Dictionary<string, float> Relationships = new Dictionary<string, float>();
        /// <summary>Cast id → NPC entity id text in this world.</summary>
        public Dictionary<string, string> CastNpcs = new Dictionary<string, string>();
        public List<string> Restrictions = new List<string>();
        public string ActiveDialogue = "";
        public string ActiveNode = "";
        public string Home = "";
        public List<string> Log = new List<string>();
        /// <summary>The ending reached (empty until the finale); the world remembers it after the credits.</summary>
        public string Ending = "";

        public MissionState Mission(string id) => Missions.Find(m => m.Id == id);

        public int Flag(string name) => Flags.TryGetValue(name, out var v) ? v : 0;
        public float Relationship(string cast) => Relationships.TryGetValue(cast, out var v) ? v : 0f;
    }

    /// <summary>
    /// Parsed effect/condition strings. Authored as "verb:arg:arg" in JSON so writers can script beats without code;
    /// every string is parsed and validated at load time (ContentLoader), so typos fail the build, not the player.
    /// </summary>
    public struct StoryOp
    {
        public string Verb;
        public string[] Args;

        public string Arg(int i) => Args != null && i < Args.Length ? Args[i] : "";

        public static StoryOp Parse(string text)
        {
            var parts = (text ?? "").Split(':');
            var args = new string[parts.Length - 1];
            Array.Copy(parts, 1, args, 0, args.Length);
            return new StoryOp { Verb = parts[0].Trim(), Args = args };
        }

        public static readonly string[] EffectVerbs =
            { "flag", "rel", "money", "pay", "mission", "time", "storm", "anomaly", "cutscene", "timejump", "part", "history", "item", "restrict", "cast", "say",
              "ordinance", "election", "disaster", "fire", "register", "opinion", "rep", "transfer", "ending" };

        public static readonly string[] ConditionVerbs = { "flag", "rel", "money", "mission", "hour", "item", "part", "power", "rep", "ordinance", "registered", "wanted" };

        /// <summary>Compares "a OP b" for the operators ≥ ≤ = &gt; &lt; (written >=, <=, =, >, <).</summary>
        public static bool Compare(double left, string expression, out bool valid)
        {
            valid = true;
            foreach (var op in new[] { ">=", "<=", "=", ">", "<" })
            {
                var i = expression.IndexOf(op, StringComparison.Ordinal);
                if (i < 0) continue;
                if (!double.TryParse(expression.Substring(i + op.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var right)) break;
                switch (op)
                {
                    case ">=": return left >= right;
                    case "<=": return left <= right;
                    case "=": return Math.Abs(left - right) < 1e-9;
                    case ">": return left > right;
                    default: return left < right;
                }
            }
            valid = false;
            return false;
        }

        /// <summary>Splits "name>=3" into ("name", ">=3").</summary>
        public static (string name, string comparison) SplitComparison(string s)
        {
            for (var i = 0; i < s.Length; i++)
                if (s[i] == '>' || s[i] == '<' || s[i] == '=') return (s.Substring(0, i), s.Substring(i));
            return (s, "");
        }
    }
}
