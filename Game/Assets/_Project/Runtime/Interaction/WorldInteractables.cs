using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Presentation;
using UnityEngine;

namespace HeroGame.Runtime.Interaction
{
    /// <summary>Hinged door. Locked doors respect ownership: owners pass, others need to break in (crime hook).</summary>
    public sealed class DoorInteractable : Interactable
    {
        public Transform Hinge;
        public float OpenAngle = 95f;
        public float Speed = 240f;
        public bool Locked;
        public PlaceMarker OwnerPlace;

        private bool _open;
        private float _angle;

        public override InteractionCategory Categories => InteractionCategory.Usable | InteractionCategory.Enterable | InteractionCategory.Breakable;

        public override string GetPrompt(InteractionContext context)
        {
            if (Locked && !ActorOwnsPlace()) return "Locked";
            return _open ? "Close door" : "Open door";
        }

        public override bool CanInteract(InteractionContext context) => base.CanInteract(context) && (!Locked || ActorOwnsPlace());

        public override void Interact(InteractionContext context) => _open = !_open;

        private void Update()
        {
            var target = _open ? OpenAngle : 0f;
            _angle = Mathf.MoveTowards(_angle, target, Speed * Time.deltaTime);
            var hinge = Hinge != null ? Hinge : transform;
            hinge.localRotation = Quaternion.Euler(0f, _angle, 0f);
        }

        private bool ActorOwnsPlace()
        {
            if (OwnerPlace == null || !ServiceRegistry.TryGet<GameSession>(out var session) || session.LocalCharacter == null) return false;
            var place = OwnerPlace.Resolve(session.World);
            return place != null && place.Property.IsValid && session.World.Ownership.OwnerOf(place.Property) == session.LocalCharacter.CharacterId;
        }
    }

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

    /// <summary>Counter/register at a business: shows live trading data (the business keeps running offline).</summary>
    public sealed class BusinessCounter : Interactable
    {
        public PlaceMarker Place;
        public string Report { get; private set; } = "";

        public override InteractionCategory Categories => InteractionCategory.Usable | InteractionCategory.Talkable;

        public override string GetPrompt(InteractionContext context) => "Check in at the counter";

        public override void Interact(InteractionContext context)
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || Place == null) return;
            var place = Place.Resolve(session.World);
            if (place == null) return;
            foreach (var b in session.World.Businesses.Values)
            {
                if (b.Place != place.Id) continue;
                var last = b.Reports.Count > 0 ? b.Reports[b.Reports.Count - 1] : null;
                Report = b.Name + " — reputation " + (b.Reputation * 100f).ToString("0") + "%, staff " + b.Staff +
                         (last != null ? ", yesterday " + last.Customers + " customers, revenue " + new Money(last.RevenueCents) : "");
                Debug.Log("[Business] " + Report);
                return;
            }
            Report = "Nobody is working here.";
        }
    }
}
