using UnityEngine;

namespace HeroGame.Runtime.Crime
{
    using HeroGame.Core.Business;
    using HeroGame.Core.Characters;
    using HeroGame.Core.Crime;
    using HeroGame.Core.Property;
    using HeroGame.Core.Simulation;
    using HeroGame.Runtime.Bootstrap;
    using HeroGame.Runtime.Interaction;
    using HeroGame.Runtime.Presentation;
    using HeroGame.Runtime.UI;
    using HeroGame.Runtime.Vehicles;

    public sealed class BreakInPoint : PlaceInteractable
    {
        public override InteractionCategory Categories => InteractionCategory.Breakable;

        public override string GetPrompt(InteractionContext context)
        {
            if (!TryContext(out var session, out var me, out var place)) return "";
            var p = PropertyAt(session, place);
            if (p == null || session.World.Ownership.IsOwnedBy(p.Id, me.CharacterId)) return "";
            return "Break in";
        }

        public override bool CanInteract(InteractionContext context) => base.CanInteract(context) && !string.IsNullOrEmpty(GetPrompt(context));

        public override void Interact(InteractionContext context)
        {
            if (!TryContext(out var session, out var me, out var place)) return;
            var p = PropertyAt(session, place);
            if (p == null) return;
            PlayerConcealment.Say(session.World.Crimes.Burglary(me, p, PlayerConcealment.For(me)));
        }
    }
}
