using System;
using System.Collections.Generic;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// Phone app icons drawn in code from signed-distance shapes (speech bubble, newspaper, bank, house, bag, map pin,
    /// radio, ripple, percent, shield, briefcase): white glyphs with anti-aliased edges on a transparent background,
    /// laid over each app's tile colour. Placeholder art (docs/ASSET_TRACKER.md) until an icon set is commissioned.
    /// </summary>
    public static class PhoneIcons
    {
        public const int Size = 128;

        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();

        private static readonly Dictionary<string, Color> Tiles = new Dictionary<string, Color>
        {
            { "Messages", new Color32(46, 160, 98, 255) },
            { "News", new Color32(196, 72, 60, 255) },
            { "Bank", new Color32(32, 108, 168, 255) },
            { "Properties", new Color32(214, 138, 40, 255) },
            { "Inventory", new Color32(118, 88, 168, 255) },
            { "Map", new Color32(38, 148, 138, 255) },
            { "Radio", new Color32(178, 60, 118, 255) },
            { "Ripple", new Color32(52, 118, 218, 255) },
            { "Loans", new Color32(96, 120, 56, 255) },
            { "Insurance", new Color32(62, 88, 140, 255) },
            { "Businesses", new Color32(150, 98, 48, 255) },
            { "Wardrobe", new Color32(168, 64, 92, 255) },
        };

        /// <summary>The tile colour behind an app's glyph.</summary>
        public static Color TileColor(string app) => Tiles.TryGetValue(app, out var c) ? c : new Color32(27, 79, 114, 255);

        /// <summary>The app's glyph (cached), or null for an app without one.</summary>
        public static Texture2D For(string app)
        {
            if (Cache.TryGetValue(app, out var cached) && cached != null) return cached;
            var shape = Shape(app);
            if (shape == null) return null;
            var tex = Render(shape);
            tex.name = "PhoneIcon_" + app;
            Cache[app] = tex;
            return tex;
        }

        private static Texture2D Render(Func<float, float, float> shape)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[Size * Size];
            const float px = 1f / Size;
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                var d = shape((x + 0.5f) * px, (y + 0.5f) * px);
                var a = Mathf.Clamp01(0.5f - d / px);
                pixels[y * Size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            return tex;
        }

        // ------------------------------------------------------------------ glyphs (unit square, y up)

        private static Func<float, float, float> Shape(string app)
        {
            switch (app)
            {
                case "Messages":
                    return Sub(Union(Box(0.5f, 0.56f, 0.27f, 0.19f, 0.08f), Tri(0.33f, 0.42f, 0.47f, 0.42f, 0.3f, 0.24f)),
                        Union(Circle(0.38f, 0.56f, 0.035f), Circle(0.5f, 0.56f, 0.035f), Circle(0.62f, 0.56f, 0.035f)));
                case "News":
                    return Union(Sub(Box(0.5f, 0.5f, 0.25f, 0.29f, 0.04f), Box(0.5f, 0.5f, 0.2f, 0.24f, 0.01f)),
                        Box(0.5f, 0.64f, 0.14f, 0.045f, 0.01f),
                        Seg(0.36f, 0.51f, 0.64f, 0.51f, 0.03f), Seg(0.36f, 0.43f, 0.64f, 0.43f, 0.03f), Seg(0.36f, 0.35f, 0.56f, 0.35f, 0.03f));
                case "Bank":
                    return Union(Tri(0.2f, 0.62f, 0.8f, 0.62f, 0.5f, 0.8f),
                        Box(0.32f, 0.45f, 0.035f, 0.12f, 0.01f), Box(0.44f, 0.45f, 0.035f, 0.12f, 0.01f),
                        Box(0.56f, 0.45f, 0.035f, 0.12f, 0.01f), Box(0.68f, 0.45f, 0.035f, 0.12f, 0.01f),
                        Box(0.5f, 0.28f, 0.3f, 0.035f, 0.01f));
                case "Properties":
                    return Sub(Union(Tri(0.18f, 0.5f, 0.82f, 0.5f, 0.5f, 0.8f), Box(0.5f, 0.36f, 0.23f, 0.16f, 0.02f)),
                        Box(0.5f, 0.29f, 0.055f, 0.1f, 0.015f));
                case "Inventory":
                    return Union(Box(0.5f, 0.41f, 0.25f, 0.2f, 0.06f),
                        Intersect(Ring(0.5f, 0.61f, 0.115f, 0.055f), Above(0.6f)));
                case "Map":
                    return Sub(Union(Circle(0.5f, 0.6f, 0.18f), Tri(0.35f, 0.53f, 0.65f, 0.53f, 0.5f, 0.2f)), Circle(0.5f, 0.6f, 0.07f));
                case "Radio":
                    return Union(Sub(Box(0.5f, 0.41f, 0.29f, 0.19f, 0.05f), Union(Ring(0.38f, 0.41f, 0.08f, 0.04f),
                            Seg(0.6f, 0.47f, 0.7f, 0.47f, 0.03f), Seg(0.6f, 0.37f, 0.7f, 0.37f, 0.03f))),
                        Seg(0.32f, 0.6f, 0.62f, 0.8f, 0.035f));
                case "Ripple":
                    return Union(Circle(0.5f, 0.5f, 0.075f), Ring(0.5f, 0.5f, 0.18f, 0.05f), Ring(0.5f, 0.5f, 0.3f, 0.05f));
                case "Loans":
                    return Union(Ring(0.36f, 0.64f, 0.085f, 0.05f), Ring(0.64f, 0.36f, 0.085f, 0.05f), Seg(0.68f, 0.72f, 0.32f, 0.28f, 0.055f));
                case "Insurance":
                    return Sub(Union(Box(0.5f, 0.62f, 0.23f, 0.15f, 0.03f), Tri(0.27f, 0.49f, 0.73f, 0.49f, 0.5f, 0.18f)),
                        Union(Seg(0.4f, 0.5f, 0.47f, 0.42f, 0.045f), Seg(0.47f, 0.42f, 0.61f, 0.6f, 0.045f)));
                case "Businesses":
                    return Sub(Union(Box(0.5f, 0.41f, 0.29f, 0.19f, 0.04f), Sub(Box(0.5f, 0.63f, 0.11f, 0.07f, 0.03f), Box(0.5f, 0.62f, 0.065f, 0.04f, 0.01f))),
                        Seg(0.21f, 0.45f, 0.79f, 0.45f, 0.025f));
                case "Wardrobe":
                    return Union(Seg(0.5f, 0.62f, 0.2f, 0.36f, 0.04f), Seg(0.2f, 0.36f, 0.8f, 0.36f, 0.04f), Seg(0.8f, 0.36f, 0.5f, 0.62f, 0.04f),
                        Seg(0.5f, 0.62f, 0.5f, 0.68f, 0.035f), Intersect(Ring(0.5f, 0.74f, 0.06f, 0.035f), Above(0.72f)));
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ signed distances (negative inside)

        private static Func<float, float, float> Circle(float cx, float cy, float r) =>
            (x, y) => Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r;

        private static Func<float, float, float> Ring(float cx, float cy, float r, float width) =>
            (x, y) => Mathf.Abs(Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - r) - width * 0.5f;

        private static Func<float, float, float> Box(float cx, float cy, float hx, float hy, float radius) =>
            (x, y) =>
            {
                var qx = Mathf.Abs(x - cx) - (hx - radius);
                var qy = Mathf.Abs(y - cy) - (hy - radius);
                var ox = Mathf.Max(qx, 0f);
                var oy = Mathf.Max(qy, 0f);
                return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
            };

        private static Func<float, float, float> Seg(float ax, float ay, float bx, float by, float width) =>
            (x, y) =>
            {
                float px = x - ax, py = y - ay, dx = bx - ax, dy = by - ay;
                var h = Mathf.Clamp01((px * dx + py * dy) / (dx * dx + dy * dy));
                px -= dx * h;
                py -= dy * h;
                return Mathf.Sqrt(px * px + py * py) - width * 0.5f;
            };

        /// <summary>A filled triangle (the largest distance to its three edge lines: exact inside, close enough outside for anti-aliasing).</summary>
        private static Func<float, float, float> Tri(float ax, float ay, float bx, float by, float cx, float cy)
        {
            var sign = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax) > 0f ? -1f : 1f;
            return (x, y) => Mathf.Max(Edge(ax, ay, bx, by, x, y, sign), Mathf.Max(Edge(bx, by, cx, cy, x, y, sign), Edge(cx, cy, ax, ay, x, y, sign)));
        }

        private static float Edge(float ax, float ay, float bx, float by, float x, float y, float sign)
        {
            float ex = bx - ax, ey = by - ay;
            var len = Mathf.Sqrt(ex * ex + ey * ey);
            return sign * ((x - ax) * ey - (y - ay) * ex) / len * -1f;
        }

        private static Func<float, float, float> Above(float yMin) => (x, y) => yMin - y;

        private static Func<float, float, float> Union(params Func<float, float, float>[] shapes) =>
            (x, y) =>
            {
                var d = float.MaxValue;
                foreach (var s in shapes) d = Mathf.Min(d, s(x, y));
                return d;
            };

        private static Func<float, float, float> Intersect(Func<float, float, float> a, Func<float, float, float> b) =>
            (x, y) => Mathf.Max(a(x, y), b(x, y));

        private static Func<float, float, float> Sub(Func<float, float, float> a, Func<float, float, float> b) =>
            (x, y) => Mathf.Max(a(x, y), -b(x, y));
    }
}
