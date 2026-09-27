using HeroGame.Core.Business;
using HeroGame.Core.Foundation;
using HeroGame.Runtime.Bootstrap;
using HeroGame.Runtime.Presentation;
using UnityEngine;

namespace HeroGame.Runtime.Interaction
{
    /// <summary>Counter/register at a business: shows live trading data (the business keeps running offline).</summary>
    public sealed class BusinessCounter : Interactable
    {
        public PlaceMarker Place;
        public string Report { get; private set; } = "";

        public override InteractionCategory Categories => InteractionCategory.Usable | InteractionCategory.Talkable;

        public override string GetPrompt(InteractionContext context)
        {
            var b = Business(out var session);
            if (b == null || session.LocalCharacter == null) return "Check in at the counter";
            if (session.World.BusinessOps.CanManage(b, session.LocalCharacter.CharacterId)) return "Open the office of " + b.Name;
            return b.ForSale || !session.World.Ownership.OwnerOf(b.Id).IsValid ? b.Name + " is for sale — ask about it" : "Check in at the counter";
        }

        private BusinessRecord Business(out GameSession session)
        {
            session = null;
            if (!ServiceRegistry.TryGet(out session) || Place == null) return null;
            var place = Place.Resolve(session.World);
            if (place == null) return null;
            foreach (var b in session.World.Businesses.Values) if (b.Place == place.Id) return b;
            return null;
        }

        public override void Interact(InteractionContext context)
        {
            if (!ServiceRegistry.TryGet<GameSession>(out var session) || Place == null) return;
            var place = Place.Resolve(session.World);
            if (place == null) return;
            foreach (var b in session.World.Businesses.Values)
            {
                if (b.Place != place.Id) continue;
                var me = session.LocalCharacter;
                if (me != null && (session.World.BusinessOps.CanManage(b, me.CharacterId) || b.ForSale || !session.World.Ownership.OwnerOf(b.Id).IsValid))
                {
                    UI.BusinessPanel.Open(b);
                    return;
                }
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
