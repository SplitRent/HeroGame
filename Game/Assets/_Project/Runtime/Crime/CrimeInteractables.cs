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
    /// <summary>
    /// What the police and witnesses can see of the player right now: a worn ski mask hides the face, and the
    /// alias costume makes evidence point at the alias instead of the civilian identity (GDD §29).
    /// </summary>
    public static class PlayerConcealment
    {
        public static bool MaskOn;

        public static ConcealmentState For(ServerCharacter c)
        {
            var state = new ConcealmentState();
            if (c == null) return state;
            var hasMask = c.Inventory.Exists(s => s.ItemId == "ski_mask");
            if (MaskOn && hasMask) state.FaceConcealment = 0.95f;
            state.InAliasCostume = c.Alias != null && !string.IsNullOrEmpty(c.Alias.CostumeOutfitId) && c.CurrentOutfit == c.Alias.CostumeOutfitId;
            if (state.InAliasCostume) state.FaceConcealment = Mathf.Max(state.FaceConcealment, 0.8f);
            return state;
        }

        public static void Say(CrimeResult r) => SubtitleFeed.Say("", r.Message + (r.Reported ? " Someone called the police." : ""), 4f);
    }

    /// <summary>Base for interactables tied to a place/business/property.</summary>
    public abstract class PlaceInteractable : Interactable
    {
        public PlaceMarker Place;

        protected bool TryContext(out GameSession session, out ServerCharacter me, out Core.World.Place place)
        {
            me = null;
            place = null;
            if (!ServiceRegistry.TryGet(out session) || session.LocalCharacter == null || Place == null) return false;
            me = session.LocalCharacter;
            place = Place.Resolve(session.World);
            return place != null;
        }

        protected static BusinessRecord BusinessAt(GameSession session, Core.World.Place place)
        {
            foreach (var b in session.World.Businesses.Values) if (b.Place == place.Id) return b;
            return null;
        }

        protected static PropertyRecord PropertyAt(GameSession session, Core.World.Place place) =>
            place.Property.IsValid ? session.World.Properties.Get(place.Property) : null;
    }

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

    public sealed class RegisterRobbery : PlaceInteractable
    {
        public override InteractionCategory Categories => InteractionCategory.Stealable;
        public override string GetPrompt(InteractionContext context) => "Rob the register";

        public override void Interact(InteractionContext context)
        {
            if (!TryContext(out var session, out var me, out var place)) return;
            var b = BusinessAt(session, place);
            if (b == null) return;
            PlayerConcealment.Say(session.World.Crimes.RobStore(me, b, PlayerConcealment.For(me)));
        }
    }

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

    public sealed class PoliceDesk : PlaceInteractable
    {
        public override InteractionCategory Categories => InteractionCategory.Talkable;
        public override string GetPrompt(InteractionContext context) => "Front desk (fines, bail, cases)";
        public override void Interact(InteractionContext context) => JusticePanel.Open();
    }
}
