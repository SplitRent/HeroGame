using HeroGame.Core.Business;
using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Presentation;
using UnityEngine;

namespace HeroGame.Runtime.Interaction
{
    /// <summary>
    /// "For Sale" sign: buys the linked property through the same atomic, journaled transaction path a
    /// multiplayer request uses (GDD §173). Demonstrates property + economy + persistence end to end.
    /// </summary>
    public sealed class PropertyForSaleSign : Interactable
    {
        public PlaceMarker Place;
        public string LastMessage { get; private set; } = "";

        public override InteractionCategory Categories => InteractionCategory.Purchasable;

        public override string GetPrompt(InteractionContext context)
        {
            var property = Resolve(out var session);
            if (property == null) return "";
            if (session.World.Ownership.OwnerOf(property.Id) == session.LocalCharacter?.CharacterId) return "You own " + property.Address;
            if (!property.ForSale) return property.Address + " (not for sale)";
            var price = new Money(property.ListingPriceCents);
            return "Buy " + property.Address + " for " + price + " (+" + session.World.Taxes.TransferTax(price) + " transfer tax)";
        }

        public override bool CanInteract(InteractionContext context)
        {
            var property = Resolve(out _);
            return base.CanInteract(context) && property != null && property.ForSale;
        }

        public override void Interact(InteractionContext context)
        {
            var property = Resolve(out var session);
            if (property == null) return;
            var result = session.BuyProperty(property.Id);
            LastMessage = result.Success ? "Purchased " + property.Address + "." : "Purchase failed: " + result.Error;
            Debug.Log("[Property] " + LastMessage);
            if (result.Success) session.Save();
        }

        private Core.Property.PropertyRecord Resolve(out GameSession session)
        {
            if (!ServiceRegistry.TryGet(out session) || Place == null) return null;
            var place = Place.Resolve(session.World);
            return place != null && place.Property.IsValid ? session.World.Properties.Get(place.Property) : null;
        }
    }
}
