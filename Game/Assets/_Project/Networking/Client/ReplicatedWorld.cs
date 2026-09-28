using System;
using System.Collections.Generic;

namespace HeroGame.Networking.Client
{
    using HeroGame.Core.Building;
    using HeroGame.Core.Foundation;
    using HeroGame.Networking.Protocol;

    /// <summary>
    /// The client's mirror of shared world state the server replicates (<see cref="WorldDelta"/>): broken street props,
    /// fires, player-owned / for-sale / damaged / rebuilt properties, and fetched building layouts. Presentation reads
    /// this instead of its local simulation while online. Anything absent is in its default state (intact, not burning,
    /// not player-owned, not for sale, undamaged, generated layout).
    /// </summary>
    public sealed class ReplicatedWorld
    {
        private readonly Dictionary<int, byte> _props = new Dictionary<int, byte>();
        private readonly Dictionary<EntityId, FireChange> _fires = new Dictionary<EntityId, FireChange>();
        private readonly Dictionary<EntityId, PropertyChange> _properties = new Dictionary<EntityId, PropertyChange>();
        private readonly Dictionary<EntityId, (int version, BuildingLayout layout)> _layouts = new Dictionary<EntityId, (int, BuildingLayout)>();

        /// <summary>True once the first full state has arrived.</summary>
        public bool Ready { get; private set; }

        public event Action<int, byte> PropChanged;
        public event Action<FireChange> FireChanged;
        public event Action<PropertyChange> PropertyChanged;
        /// <summary>A newer building layout was received (presentation should rebuild that interior).</summary>
        public event Action<EntityId, BuildingLayout> LayoutReceived;

        public byte PropState(int id) => _props.TryGetValue(id, out var s) ? s : (byte)0;
        public IEnumerable<KeyValuePair<int, byte>> BrokenProps => _props;
        public IEnumerable<FireChange> Fires => _fires.Values;
        public int FireCount => _fires.Count;

        public PropertyChange Property(EntityId id) =>
            _properties.TryGetValue(id, out var p) ? p : new PropertyChange { Property = id };

        public IEnumerable<PropertyChange> Properties => _properties.Values;

        /// <summary>The building layout for a property if the client holds the current version; null means use the generated one or fetch.</summary>
        public BuildingLayout Layout(EntityId property)
        {
            var wanted = Property(property).LayoutVersion;
            return _layouts.TryGetValue(property, out var l) && l.version == wanted ? l.layout : null;
        }

        /// <summary>The server rebuilt this building and the client does not have that version yet.</summary>
        public bool NeedsLayout(EntityId property)
        {
            var wanted = Property(property).LayoutVersion;
            return wanted > 0 && (!_layouts.TryGetValue(property, out var l) || l.version < wanted);
        }

        public void Apply(WorldDelta delta)
        {
            if (delta.Full)
            {
                // Tell listeners about everything that goes back to default, then load the new state.
                var oldProps = new List<int>(_props.Keys);
                var oldFires = new List<FireChange>(_fires.Values);
                var oldProperties = new List<EntityId>(_properties.Keys);
                _props.Clear();
                _fires.Clear();
                _properties.Clear();
                foreach (var id in oldProps) PropChanged?.Invoke(id, 0);
                foreach (var f in oldFires) FireChanged?.Invoke(new FireChange { Incident = f.Incident, Position = f.Position, Intensity = 0f });
                foreach (var id in oldProperties) PropertyChanged?.Invoke(new PropertyChange { Property = id });
                Ready = true;
            }
            foreach (var p in delta.Props)
            {
                if (p.State == 0) _props.Remove(p.Id);
                else _props[p.Id] = p.State;
                PropChanged?.Invoke(p.Id, p.State);
            }
            foreach (var f in delta.Fires)
            {
                if (f.Intensity <= 0f) _fires.Remove(f.Incident);
                else _fires[f.Incident] = f;
                FireChanged?.Invoke(f);
            }
            foreach (var p in delta.Properties)
            {
                if (p.IsDefault) _properties.Remove(p.Property);
                else _properties[p.Property] = p;
                PropertyChanged?.Invoke(p);
            }
        }

        /// <summary>Stores a fetched layout. Malformed or stale data is ignored.</summary>
        public bool Apply(LayoutData data)
        {
            if (_layouts.TryGetValue(data.Property, out var have) && have.version > data.Version) return false;
            BuildingLayout layout;
            try
            {
                layout = Newtonsoft.Json.JsonConvert.DeserializeObject<BuildingLayout>(data.Json);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
            if (layout == null) return false;
            _layouts[data.Property] = (data.Version, layout);
            LayoutReceived?.Invoke(data.Property, layout);
            return true;
        }
    }
}
