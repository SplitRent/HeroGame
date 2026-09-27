using HeroGame.Runtime.Crime;
using HeroGame.Runtime.Interaction;
using HeroGame.Runtime.UI;

namespace HeroGame.Runtime.Civic
{
    /// <summary>City Hall counter: budget, council agenda, elections and power registration (GDD §24–27).</summary>
    public sealed class CityHallDesk : PlaceInteractable
    {
        public override InteractionCategory Categories => InteractionCategory.Talkable;
        public override string GetPrompt(InteractionContext context) => "City Hall (elections, council, registration)";
        public override void Interact(InteractionContext context) => CivicPanel.Open();
    }
}
