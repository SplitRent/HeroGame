using System;
using System.Collections.Generic;

namespace HeroGame.Core.World
{
    using HeroGame.Core.Foundation;

    public enum PropState
    {
        Intact,
        Damaged,
        Destroyed,
    }

    /// <summary>A kind of breakable street furniture (destructibles.json).</summary>
    [Serializable]
    public sealed class DestructibleKind
    {
        public string Id = "";
        public string DisplayName = "";
        /// <summary>Metal, Wood, Concrete, Glass — drives debris and power interactions.</summary>
        public string Material = "Metal";
        public float MaxHealth = 1f;
        public long RepairCostCents = 100000;
        /// <summary>Public works fixes higher priorities first (signals before benches).</summary>
        public int RepairPriority;
        /// <summary>0..1: how well it stands up to hurricane winds.</summary>
        public float WindResistance = 0.8f;
        /// <summary>What breaking it does to the world: Darkness, SignalOut, WaterMain, or nothing.</summary>
        public string Effect = "";
        /// <summary>Art asset name from the Blender kit (empty: greybox shape).</summary>
        public string Mesh = "";
    }

    /// <summary>One placed destructible. Compact: tens of thousands exist in a full city.</summary>
    [Serializable]
    public sealed class PropInstance
    {
        public int Id;
        public string Kind = "";
        public float X;
        public float Z;
        /// <summary>Facing (degrees) for presentation.</summary>
        public float Yaw;
        public float Health = 1f;
        public PropState State;
        public long BrokenDay = -1;
        /// <summary>Road node for traffic signals (-1 otherwise).</summary>
        public int RoadNode = -1;

        public WorldPosition Position => new WorldPosition(X, 0f, Z);
    }

    [Serializable]
    public sealed class DestructionState
    {
        public List<PropInstance> Props = new List<PropInstance>();
        public bool Generated;
        public int Repaired;
        public int Destroyed;
        /// <summary>Properties that structurally collapsed and when (property id → day), for rebuild scheduling.</summary>
        public Dictionary<string, long> Collapsed = new Dictionary<string, long>();
    }
}
