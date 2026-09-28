using UnityEngine;

namespace HeroGame.Runtime.Population
{
    using HeroGame.Core.Social;
    using HeroGame.Runtime.Bootstrap;
    using HeroGame.Runtime.Interaction;
    using HeroGame.Runtime.UI;
    using HeroGame.Runtime.Player;

    /// <summary>
    /// Talk to a materialised NPC. The conversation runs through the core InteractionService, so the NPC's
    /// persistent memory changes and they will remember it next time — on any day, after any restart.
    /// Players also hear occasional ambient lines when walking past people who know them.
    /// </summary>
    [RequireComponent(typeof(NpcAvatar))]
    public sealed class NpcTalkInteractable : Interactable
    {
        public float AmbientRadius = 3.5f;
        public float AmbientCooldown = 25f;

        private NpcAvatar _avatar;
        private float _nextAmbient;
        private int _talkCount;

        public override InteractionCategory Categories => InteractionCategory.Talkable;

        private void Awake() => _avatar = GetComponent<NpcAvatar>();

        public override string GetPrompt(InteractionContext context)
        {
            if (!TryResolve(out var session, out var npc)) return "";
            var memory = session.LocalCharacter != null ? npc.MemoryOf(session.LocalCharacter.CharacterId, false, 0) : null;
            var knows = BarkSelector.FamiliarityOf(memory) >= Familiarity.Acquaintance;
            if (Crouching()) return "Pick " + (knows ? npc.FirstName + "'s" : "their") + " pocket";
            return "Talk to " + (knows ? npc.FirstName : "stranger");
        }

        public override void Interact(InteractionContext context)
        {
            if (!TryResolve(out var session, out var npc) || session.LocalCharacter == null) return;
            if (Crouching())
            {
                var theft = session.World.Crimes.Pickpocket(session.LocalCharacter, npc, transform.position.ToWorld(), Crime.PlayerConcealment.For(session.LocalCharacter));
                Crime.PlayerConcealment.Say(theft);
                if (theft.CaughtInAct) SubtitleFeed.Say(npc.FirstName, "Hey! Get your hands off me!", 3f);
                return;
            }
            // Story characters with something to say about the current mission take over the conversation.
            if (session.Story != null && session.Story.TalkTo(npc.Id)) return;
            // Alternate greeting and small talk so a conversation feels like one.
            var kind = _talkCount++ % 2 == 0 ? InteractionKind.Greet : InteractionKind.SmallTalk;
            var identity = ServiceRegistry.TryGet<Core.Characters.AccountProfile>(out var account) ? account.Character : null;
            var outcome = session.World.Conversations.Interact(npc, session.LocalCharacter, identity, kind);
            SubtitleFeed.Say(npc.FirstName, outcome.Line);
        }

        private IPlayerInputSource _input;

        private bool Crouching()
        {
            if (_input == null) _input = PlayerInputRegistry.Create();
            return _input.GameplayEnabled && _input.Read().CrouchHeld;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextAmbient || Camera.main == null) return;
            _nextAmbient = Time.unscaledTime + AmbientCooldown * Random.Range(0.8f, 1.4f);
            if (!TryResolve(out var session, out var npc) || session.LocalCharacter == null) return;
            var player = session.LocalCharacter.LastPosition;
            var here = transform.position;
            if (new Vector2(here.x - player.X, here.z - player.Z).sqrMagnitude > AmbientRadius * AmbientRadius) return;
            var identity = ServiceRegistry.TryGet<Core.Characters.AccountProfile>(out var account) ? account.Character : null;
            if (session.World.Conversations.Ambient(npc, session.LocalCharacter, identity, out var line)) SubtitleFeed.Say(npc.FirstName, line, 3f);
        }

        private bool TryResolve(out GameSession session, out Core.Population.NpcRecord npc)
        {
            npc = null;
            if (!ServiceRegistry.TryGet(out session) || !_avatar.NpcId.IsValid) return false;
            npc = session.World.Population.Get(_avatar.NpcId);
            return npc != null;
        }
    }
}
