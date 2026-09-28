using UnityEngine;

namespace HeroGame.Runtime.Presentation
{
    using HeroGame.Core.World;
    using SimWorld = HeroGame.Core.Simulation.World;

    /// <summary>
    /// Links a scene object (building, lot, interior) to its simulation <see cref="Place"/>. The key is
    /// the place's name/address from the layout data, which is stable across servers; the entity id
    /// differs per world and is resolved at runtime.
    /// </summary>
    public sealed class PlaceMarker : MonoBehaviour
    {
        public string PlaceName = "";
        public PlaceKind Kind;

        private Place _cached;
        private SimWorld _cachedFor;

        public Place Resolve(SimWorld world)
        {
            if (world == null) return null;
            if (_cachedFor == world && _cached != null) return _cached;
            _cachedFor = world;
            _cached = null;
            _cached = world.Geography.FindPlaceByName(PlaceName);
            return _cached;
        }
    }
}
