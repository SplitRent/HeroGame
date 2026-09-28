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
}
