using System;

namespace HeroGame.Core.Foundation
{
    /// <summary>
    /// Engine-agnostic world position in metres (Unity axes: X east, Y up, Z north).
    /// The core never references UnityEngine.Vector3; the runtime layer converts.
    /// </summary>
    [Serializable]
    public struct WorldPosition : IEquatable<WorldPosition>
    {
        public float X;
        public float Y;
        public float Z;

        public WorldPosition(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static float DistanceXZ(WorldPosition a, WorldPosition b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        public static float DistanceSquaredXZ(WorldPosition a, WorldPosition b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return dx * dx + dz * dz;
        }

        public bool Equals(WorldPosition other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is WorldPosition other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public override string ToString() => "(" + X.ToString("0.0") + ", " + Y.ToString("0.0") + ", " + Z.ToString("0.0") + ")";
    }
}
