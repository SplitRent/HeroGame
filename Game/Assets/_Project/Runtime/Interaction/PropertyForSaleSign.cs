using UnityEngine;

namespace HeroGame.Runtime.Interaction
{
    using HeroGame.Core.Business;
    using HeroGame.Core.Foundation;
    using HeroGame.Runtime.Bootstrap;
    using HeroGame.Runtime.Presentation;

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
            var replica = Online.NetworkSession.Replica;
            if (replica != null)
            {
                // Online: ownership and listings come from the server.
                var view = replica.Property(property.Id);
                if (view.PlayerOwner.IsValid) return view.PlayerOwner == session.LocalCharacter?.CharacterId ? "You own " + property.Address : property.Address + " (owned by a player)";
                if (!view.ForSale) return property.Address + " (not for sale)";
                return "Buy " + property.Address + " for " + new Money(view.ListingPriceCents);
            }
            if (session.World.Ownership.OwnerOf(property.Id) == session.LocalCharacter?.CharacterId) return "You own " + property.Address;
            if (!property.ForSale) return property.Address + " (not for sale)";
            var price = new Money(property.ListingPriceCents);
            return "Buy " + property.Address + " for " + price + " (+" + session.World.Taxes.TransferTax(price) + " transfer tax)";
        }

        public override bool CanInteract(InteractionContext context)
        {
            var property = Resolve(out _);
            var replica = Online.NetworkSession.Replica;
            return base.CanInteract(context) && property != null && (replica != null ? replica.Property(property.Id).ForSale : property.ForSale);
        }

        public override void Interact(InteractionContext context)
        {
            var property = Resolve(out var session);
            if (property == null) return;
            var online = Online.NetworkSession.Current;
            if (Online.NetworkSession.Replica != null && online != null)
            {
                // The server owns the transaction; the sign updates when the ownership change is replicated.
                LastMessage = "Buying " + property.Address + "…";
                _ = online.Request("property.buy", new System.Collections.Generic.Dictionary<string, string> { ["property"] = property.Id.ToString() })
                    .ContinueWith(t => LastMessage = t.Result.Success ? "Purchased " + property.Address + "." : "Purchase failed: " + t.Result.Error,
                        System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }
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
