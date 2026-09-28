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

    public sealed class PoliceDesk : PlaceInteractable
    {
        public override InteractionCategory Categories => InteractionCategory.Talkable;
        public override string GetPrompt(InteractionContext context) => "Front desk (fines, bail, cases)";
        public override void Interact(InteractionContext context) => JusticePanel.Open();
    }
}
