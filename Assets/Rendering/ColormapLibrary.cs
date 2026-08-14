using System;
using UnityEngine;

namespace PointCloud.Rendering
{
    /// <summary>Row index into the colormap LUT atlas. Order must match <see cref="ColormapLibrary.Names"/>.</summary>
    public enum Colormap
    {
        Viridis,
        Turbo,
        Inferno,
        Magma,
        Plasma,
        Grayscale,
        /// <summary>Diverging. The default for any signed field — the sign is the interesting part.</summary>
        CoolWarm,
        /// <summary>Perceptually awful, but people ask for it by name.</summary>
        Jet,
        /// <summary>32 well-separated hues, sampled at texel centres. For label/segment ids.</summary>
        Categorical,
    }

    /// <summary>
    /// Bakes every colormap into one 256 x N RGBA32 texture, one row per map.
    ///
    /// The texture is created with <c>linear: false</c>, i.e. flagged sRGB. Published
    /// viridis/turbo/inferno tables are 8-bit sRGB values chosen to be perceptually uniform
    /// *in sRGB*, so in this linear-colour-space project the hardware conversion on sample
    /// is exactly the right thing. Marking it linear instead would require converting the
    /// tables by hand and is easy to get backwards.
    /// </summary>
    public sealed class ColormapLibrary : IDisposable
    {
        public const int Resolution = 256;

        public static readonly string[] Names =
        {
            "Viridis", "Turbo", "Inferno", "Magma", "Plasma",
            "Grayscale", "Cool-Warm", "Jet", "Categorical",
        };

        public Texture2D Lut { get; private set; }

        public int Count => Names.Length;

        /// <summary>The baked table, kept so swatches and CPU-side lookups need no GPU readback.</summary>
        readonly Color32[] _pixels;

        public ColormapLibrary()
        {
            Lut = new Texture2D(Resolution, Names.Length, TextureFormat.RGBA32, mipChain: false, linear: false)
            {
                name       = "PointCloudColormapLUT",
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
                hideFlags  = HideFlags.HideAndDontSave,
            };

            var pixels = _pixels = new Color32[Resolution * Names.Length];

            Bake(pixels, Colormap.Viridis,   Tables.Viridis);
            Bake(pixels, Colormap.Turbo,     Tables.Turbo);
            Bake(pixels, Colormap.Inferno,   Tables.Inferno);
            Bake(pixels, Colormap.Magma,     Tables.Magma);
            Bake(pixels, Colormap.Plasma,    Tables.Plasma);
            Bake(pixels, Colormap.Grayscale, Tables.Grayscale);
            Bake(pixels, Colormap.CoolWarm,  Tables.CoolWarm);
            Bake(pixels, Colormap.Jet,       Tables.Jet);
            BakeCategorical(pixels, Colormap.Categorical, Tables.Categorical);

            Lut.SetPixels32(pixels);
            Lut.Apply(updateMipmaps: false, makeNoLongerReadable: false);
        }

        /// <summary>A horizontal gradient strip for the colormap dropdown swatches.</summary>
        public Texture2D CreateSwatch(Colormap map, int width = 128, int height = 12)
        {
            var swatch = new Texture2D(width, height, TextureFormat.RGBA32, false, linear: false)
            {
                name       = $"Swatch_{map}",
                filterMode = map == Colormap.Categorical ? FilterMode.Point : FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
                hideFlags  = HideFlags.HideAndDontSave,
            };

            int row = (int)map * Resolution;
            var pixels = new Color32[width * height];
            for (int x = 0; x < width; x++)
            {
                int t = Mathf.Clamp(Mathf.RoundToInt((float)x / Mathf.Max(1, width - 1) * (Resolution - 1)),
                                    0, Resolution - 1);
                var c = _pixels[row + t];
                for (int y = 0; y < height; y++) pixels[y * width + x] = c;
            }

            swatch.SetPixels32(pixels);
            swatch.Apply(false, false);
            return swatch;
        }

        static void Bake(Color32[] pixels, Colormap map, byte[,] stops)
        {
            int row = (int)map * Resolution;
            int stopCount = stops.GetLength(0);

            for (int x = 0; x < Resolution; x++)
            {
                float t = x / (float)(Resolution - 1) * (stopCount - 1);
                int   i = Mathf.Min((int)t, stopCount - 2);
                float f = t - i;

                pixels[row + x] = new Color32(
                    Lerp(stops[i, 0], stops[i + 1, 0], f),
                    Lerp(stops[i, 1], stops[i + 1, 1], f),
                    Lerp(stops[i, 2], stops[i + 1, 2], f),
                    255);
            }
        }

        /// <summary>
        /// Categorical entries must not blend into each other, so each of the 32 colours
        /// occupies a contiguous block of texels and the shader samples at a texel centre.
        /// </summary>
        static void BakeCategorical(Color32[] pixels, Colormap map, byte[,] palette)
        {
            int row = (int)map * Resolution;
            int entries = palette.GetLength(0);

            for (int x = 0; x < Resolution; x++)
            {
                int i = Mathf.Min(x * entries / Resolution, entries - 1);
                pixels[row + x] = new Color32(palette[i, 0], palette[i, 1], palette[i, 2], 255);
            }
        }

        static byte Lerp(byte a, byte b, float t) => (byte)Mathf.RoundToInt(Mathf.Lerp(a, b, t));

        public void Dispose()
        {
            if (Lut == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(Lut);
            else UnityEngine.Object.DestroyImmediate(Lut);
            Lut = null;
        }

        /// <summary>
        /// Control points, sampled from the reference matplotlib / Google Turbo tables at
        /// even intervals. 256-entry linear interpolation between these is visually
        /// indistinguishable from the full tables and keeps this file readable.
        /// </summary>
        static class Tables
        {
            public static readonly byte[,] Viridis =
            {
                {68,1,84},{72,40,120},{62,74,137},{49,104,142},{38,130,142},
                {31,158,137},{53,183,121},{109,205,89},{253,231,37},
            };

            public static readonly byte[,] Turbo =
            {
                {48,18,59},{65,69,171},{57,118,240},{27,165,254},{25,206,214},
                {56,235,166},{110,254,116},{163,252,60},{208,231,41},{240,194,51},
                {253,143,32},{238,87,14},{190,30,4},
            };

            public static readonly byte[,] Inferno =
            {
                {0,0,4},{31,12,72},{85,15,109},{136,34,106},{186,54,85},
                {227,89,51},{249,140,10},{249,201,50},{252,255,164},
            };

            public static readonly byte[,] Magma =
            {
                {0,0,4},{28,16,68},{79,18,123},{129,37,129},{181,54,122},
                {229,80,100},{251,135,97},{254,194,135},{252,253,191},
            };

            public static readonly byte[,] Plasma =
            {
                {13,8,135},{75,3,161},{125,3,168},{168,34,150},{203,70,121},
                {229,107,93},{248,148,65},{253,195,40},{240,249,33},
            };

            public static readonly byte[,] Grayscale = { {0,0,0},{255,255,255} };

            public static readonly byte[,] CoolWarm = { {59,76,192},{221,221,221},{180,4,38} };

            public static readonly byte[,] Jet =
            {
                {0,0,143},{0,0,255},{0,255,255},{255,255,0},{255,0,0},{128,0,0},
            };

            /// <summary>Glasbey-style: maximally distinct hues, safe against adjacent-label confusion.</summary>
            public static readonly byte[,] Categorical =
            {
                {230,25,75},{60,180,75},{255,225,25},{0,130,200},{245,130,48},
                {145,30,180},{70,240,240},{240,50,230},{210,245,60},{250,190,212},
                {0,128,128},{220,190,255},{170,110,40},{255,250,200},{128,0,0},
                {170,255,195},{128,128,0},{255,215,180},{0,0,128},{128,128,128},
                {255,255,255},{255,105,180},{100,200,150},{200,100,50},{50,100,200},
                {180,180,60},{90,60,150},{240,160,90},{60,140,90},{200,60,140},
                {110,180,220},{160,80,60},
            };
        }
    }
}
