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
using UnityEngine;

namespace HeroGame.Runtime.Crime
{
    /// <summary>A fence (docks) buys stolen goods; at a garage the back door is a chop shop for stolen cars parked nearby.</summary>
    public sealed class FenceContact : PlaceInteractable
    {
        public bool ChopShop;
        public float VehicleRadius = 12f;

        public override InteractionCategory Categories => InteractionCategory.Talkable;

        public override string GetPrompt(InteractionContext context)
        {
            if (!TryContext(out var session, out var me, out _)) return "";
            if (ChopShop) return StolenCarNearby(me) != null ? "Sell the car for parts" : "";
            var offer = session.World.Crimes.FenceOffer(me);
            return offer.Cents > 0 ? "Sell goods to the fence (" + offer + ")" : "";
        }

        public override bool CanInteract(InteractionContext context) => base.CanInteract(context) && !string.IsNullOrEmpty(GetPrompt(context));

        public override void Interact(InteractionContext context)
        {
            if (!TryContext(out var session, out var me, out _)) return;
            var key = session.NextRequestKey(ChopShop ? "chop" : "fence");
            var result = ChopShop ? session.World.Crimes.ChopVehicle(me, StolenCarNearby(me)?.Record, key) : session.World.Crimes.SellToFence(me, key);
            SubtitleFeed.Say(ChopShop ? "Mechanic" : "Fence", result.Success ? "Pleasure doing business." : result.Error, 3f);
            if (result.Success && ChopShop)
            {
                var car = StolenCarNearby(me);
                if (car != null) Destroy(car.gameObject);
            }
        }

        private VehicleController StolenCarNearby(ServerCharacter me)
        {
            foreach (var v in FindObjectsByType<VehicleController>(FindObjectsSortMode.None))
                if (v.Record != null && v.Record.StolenBy == me.CharacterId && (v.transform.position - transform.position).sqrMagnitude < VehicleRadius * VehicleRadius) return v;
            return null;
        }
    }
}
