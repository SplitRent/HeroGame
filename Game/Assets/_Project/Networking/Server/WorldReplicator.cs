using System;
using System.Collections.Generic;

namespace HeroGame.Networking.Server
{
    using HeroGame.Core.Building;
    using HeroGame.Core.Emergency;
    using HeroGame.Core.Foundation;
    using HeroGame.Core.Simulation;
    using HeroGame.Core.World;
    using HeroGame.Networking.Protocol;

    /// <summary>
    /// Keeps the "published" view of the shared world that clients mirror (broken props, fires, player ownership, sale
    /// signs, damage, rebuilt buildings) and turns changes since the last publish into <see cref="WorldDelta"/>s.
    /// Everything is diffed against the authoritative world, so no gameplay code has to remember to announce changes.
    /// Owning thread only.
    /// </summary>
    public sealed class WorldReplicator
    {
        private readonly World _world;
        private readonly Dictionary<int, byte> _props = new Dictionary<int, byte>();
        private readonly Dictionary<EntityId, FireChange> _fires = new Dictionary<EntityId, FireChange>();
        private readonly Dictionary<EntityId, PropertyChange> _properties = new Dictionary<EntityId, PropertyChange>();
        /// <summary>Layout object each property had when its version was last bumped (layouts are copy-on-write).</summary>
        private readonly Dictionary<EntityId, BuildingLayout> _layoutSeen = new Dictionary<EntityId, BuildingLayout>();
        private readonly Dictionary<EntityId, int> _layoutVersion = new Dictionary<EntityId, int>();
        private bool _primed;

        /// <summary>Fires below this intensity are too small to show (and too noisy to replicate).</summary>
        public const float MinVisibleFire = 0.02f;
        /// <summary>Intensity changes smaller than this are not worth a message.</summary>
        public const float FireStep = 0.05f;

        public WorldReplicator(World world)
        {
            _world = world;
        }

        public int LayoutVersionOf(EntityId property) => _layoutVersion.TryGetValue(property, out var v) ? v : 0;

        /// <summary>Updates the published view and returns what changed (empty when nothing did).</summary>
        public WorldDelta Publish()
        {
            var delta = new WorldDelta();
            PublishProps(delta.Props);
            PublishFires(delta.Fires);
            PublishProperties(delta.Properties);
            _primed = true;
            return delta;
        }

        /// <summary>The whole published view, for a player who just joined (split to respect frame limits).</summary>
        public List<WorldDelta> FullState()
        {
            if (!_primed) Publish();
            var props = new List<PropChange>();
            foreach (var kv in _props) props.Add(new PropChange { Id = kv.Key, State = kv.Value });
            props.Sort((a, b) => a.Id.CompareTo(b.Id));
            var fires = new List<FireChange>(_fires.Values);
            fires.Sort((a, b) => a.Incident.CompareTo(b.Incident));
            var properties = new List<PropertyChange>(_properties.Values);
            properties.Sort((a, b) => a.Property.CompareTo(b.Property));

            var result = new List<WorldDelta> { new WorldDelta { Full = true } };
            foreach (var p in props) Room(result, d => d.Props.Count < WorldDelta.MaxProps).Props.Add(p);
            foreach (var f in fires) Room(result, d => d.Fires.Count < WorldDelta.MaxFires).Fires.Add(f);
            foreach (var p in properties) Room(result, d => d.Properties.Count < WorldDelta.MaxProperties).Properties.Add(p);
            return result;
        }

        /// <summary>Splits a large delta into frame-sized messages.</summary>
        public static List<WorldDelta> Split(WorldDelta delta)
        {
            if (delta.Props.Count <= WorldDelta.MaxProps && delta.Fires.Count <= WorldDelta.MaxFires && delta.Properties.Count <= WorldDelta.MaxProperties)
                return new List<WorldDelta> { delta };
            var result = new List<WorldDelta> { new WorldDelta { Full = delta.Full } };
            foreach (var p in delta.Props) Room(result, d => d.Props.Count < WorldDelta.MaxProps).Props.Add(p);
            foreach (var f in delta.Fires) Room(result, d => d.Fires.Count < WorldDelta.MaxFires).Fires.Add(f);
            foreach (var p in delta.Properties) Room(result, d => d.Properties.Count < WorldDelta.MaxProperties).Properties.Add(p);
            return result;
        }

        private static WorldDelta Room(List<WorldDelta> list, Func<WorldDelta, bool> fits)
        {
            var last = list[list.Count - 1];
            if (fits(last)) return last;
            last = new WorldDelta();
            list.Add(last);
            return last;
        }

        private void PublishProps(List<PropChange> changes)
        {
            foreach (var p in _world.Destructibles.All)
            {
                var state = (byte)p.State;
                _props.TryGetValue(p.Id, out var was);
                if (state == was) continue;
                if (state == 0) _props.Remove(p.Id);
                else _props[p.Id] = state;
                changes.Add(new PropChange { Id = p.Id, State = state });
            }
        }

        private void PublishFires(List<FireChange> changes)
        {
            var burning = new HashSet<EntityId>();
            foreach (var i in _world.Emergency.Incidents)
            {
                if (i.Kind != EmergencyKind.Fire || !i.Open || i.FireIntensity < MinVisibleFire) continue;
                burning.Add(i.Id);
                if (_fires.TryGetValue(i.Id, out var known) && Math.Abs(known.Intensity - i.FireIntensity) < FireStep) continue;
                var fire = new FireChange { Incident = i.Id, Position = i.Position, Intensity = i.FireIntensity };
                _fires[i.Id] = fire;
                changes.Add(fire);
            }
            List<EntityId> gone = null;
            foreach (var id in _fires.Keys)
                if (!burning.Contains(id)) (gone ?? (gone = new List<EntityId>())).Add(id);
            if (gone == null) return;
            gone.Sort();
            foreach (var id in gone)
            {
                changes.Add(new FireChange { Incident = id, Position = _fires[id].Position, Intensity = 0f });
                _fires.Remove(id);
            }
        }

        private void PublishProperties(List<PropertyChange> changes)
        {
            foreach (var p in _world.Properties.All)
            {
                // Layout versions: generated layouts are version 0; every replacement (a build) bumps the version.
                if (!_layoutSeen.TryGetValue(p.Id, out var layout)) _layoutSeen[p.Id] = p.Layout;
                else if (!ReferenceEquals(layout, p.Layout))
                {
                    _layoutSeen[p.Id] = p.Layout;
                    _layoutVersion[p.Id] = LayoutVersionOf(p.Id) + 1;
                }

                var owner = _world.Ownership.OwnerOf(p.Id);
                var view = new PropertyChange
                {
                    Property = p.Id,
                    PlayerOwner = _world.Characters.ContainsKey(owner) ? owner : EntityId.None,
                    ForSale = p.ForSale,
                    ListingPriceCents = p.ForSale ? p.ListingPriceCents : 0,
                    Damage = (byte)p.Damage,
                    LayoutVersion = LayoutVersionOf(p.Id),
                };
                if (view.IsDefault)
                {
                    if (_properties.Remove(p.Id)) changes.Add(view); // back to defaults: clients drop it
                    continue;
                }
                if (_properties.TryGetValue(p.Id, out var known) && Same(known, view)) continue;
                _properties[p.Id] = view;
                changes.Add(view);
            }
        }

        private static bool Same(PropertyChange a, PropertyChange b) =>
            a.PlayerOwner == b.PlayerOwner && a.ForSale == b.ForSale && a.ListingPriceCents == b.ListingPriceCents && a.Damage == b.Damage && a.LayoutVersion == b.LayoutVersion;
    }
}
