using UnityEngine;

namespace HeroGame.Runtime.Bootstrap
{
    using HeroGame.Core.Foundation;

    /// <summary>The only place engine-agnostic core types are converted to Unity types.</summary>
    public static class UnityConversions
    {
        public static Vector3 ToVector3(this WorldPosition p) => new Vector3(p.X, p.Y, p.Z);
        public static WorldPosition ToWorld(this Vector3 v) => new WorldPosition(v.x, v.y, v.z);

        public static Color ParseHex(string hex, Color fallback)
        {
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;
        }
    }
}
