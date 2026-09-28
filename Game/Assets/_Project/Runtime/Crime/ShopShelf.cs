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

    public sealed class ShopShelf : PlaceInteractable
    {
        public override InteractionCategory Categories => InteractionCategory.Stealable;
        public override string GetPrompt(InteractionContext context) => "Pocket something (shoplift)";

        public override void Interact(InteractionContext context)
        {
            if (!TryContext(out var session, out var me, out var place)) return;
            var b = BusinessAt(session, place);
            if (b == null) return;
            PlayerConcealment.Say(session.World.Crimes.Shoplift(me, b, PlayerConcealment.For(me)));
        }
    }
}
