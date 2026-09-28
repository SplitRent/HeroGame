using UnityEngine;

namespace HeroGame.Runtime.Presentation
{
    /// <summary>
    /// Where grass grows, baked by the greybox builder: one byte per cell over the world's ground rectangle
    /// (0 = none: road, sidewalk, building, dirt lot or water; 1 = lawn at ground level; 2 = raised park lawn).
    /// </summary>
    public sealed class GrassMask : ScriptableObject
    {
        public float MinX;
        public float MinZ;
        /// <summary>Cell size in metres.</summary>
        public float Cell = 1f;
        public int Width;
        public int Height;
        public byte[] Cells = new byte[0];

        public const float ParkHeight = 0.06f;

        /// <summary>0 = no grass, 1 = ground lawn, 2 = park lawn (raised).</summary>
        public byte At(float x, float z)
        {
            var cx = Mathf.FloorToInt((x - MinX) / Cell);
            var cz = Mathf.FloorToInt((z - MinZ) / Cell);
            if (cx < 0 || cz < 0 || cx >= Width || cz >= Height || Cells == null || Cells.Length != Width * Height) return 0;
            return Cells[cz * Width + cx];
        }
    }
}
