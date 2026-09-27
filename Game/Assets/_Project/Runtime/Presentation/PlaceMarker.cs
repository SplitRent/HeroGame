using HeroGame.Core.World;
using UnityEngine;
using SimWorld = HeroGame.Core.Simulation.World;

namespace HeroGame.Runtime.Presentation
{
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
            foreach (var p in world.Geography.Places)
            {
                if (p.Name != PlaceName) continue;
                _cached = p;
                break;
            }
            return _cached;
        }
    }
}
