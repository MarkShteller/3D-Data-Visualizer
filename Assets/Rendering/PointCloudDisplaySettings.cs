using PointCloud.Core.Data;
using Unity.Mathematics;
using UnityEngine;

namespace PointCloud.Rendering
{
    /// <summary>
    /// How a cloud is colourised. Values map 1:1 onto _ColorMode in PointCloud.shader.
    /// Never renumber — the shader switch depends on these.
    /// </summary>
    public enum PointColorMode
    {
        /// <summary>Per-point RGB. Requires Color.</summary>
        Rgb = 0,
        /// <summary>Camera-space depth ramp. The default when a cloud has no colour.</summary>
        ViewDepth = 1,
        /// <summary>Euclidean distance to the camera. Differs visibly from ViewDepth off-axis.</summary>
        RadialDistance = 2,
        /// <summary>Ramp along a world axis. The "height map" view.</summary>
        AxisRamp = 3,
        Intensity = 4,
        Label = 5,
        Confidence = 6,
        /// <summary>Generic scalar field selected by <see cref="PointCloudDisplaySettings.ScalarSlot"/>.</summary>
        Scalar = 7,
        Timestamp = 8,
        /// <summary>Normal direction as RGB. Requires Normal.</summary>
        NormalRgb = 9,
        /// <summary>Single flat colour. Also the acceptance test for EDL and for sRGB correctness.</summary>
        Flat = 10,
        /// <summary>One colour per loaded cloud, for overlay comparison.</summary>
        CloudIndex = 11,
    }

    public enum PointSizeMode
    {
        /// <summary>Constant pixel size regardless of distance.</summary>
        FixedPixels = 0,
        /// <summary>Constant world size, foreshortened correctly.</summary>
        WorldSpace = 1,
        /// <summary>World size, clamped to a pixel range. The default.</summary>
        Adaptive = 2,
    }

    public enum PointShape
    {
        /// <summary>Early-Z friendly. Default.</summary>
        Square = 0,
        /// <summary>Uses clip(), which disables early-Z. An explicit choice, not a default.</summary>
        Circle = 1,
    }

    /// <summary>
    /// Per-cloud display state. Plain mutable class bound directly to the UI; the renderer
    /// pushes it onto the material once per frame.
    /// </summary>
    public sealed class PointCloudDisplaySettings
    {
        public bool  Visible = true;
        public float Opacity = 1f;

        public PointColorMode ColorMode = PointColorMode.ViewDepth;
        public Colormap       Colormap  = Colormap.Turbo;

        /// <summary>Scalar range mapped onto the colormap. Auto-fitted per frame for the distance modes.</summary>
        public float2 ScalarRange = new(0f, 1f);

        /// <summary>Blend toward a logarithmic ramp. Useful when a scene spans several orders of depth.</summary>
        public float LogRamp;

        /// <summary>Which Scalar0..3 stream feeds <see cref="PointColorMode.Scalar"/>.</summary>
        public int ScalarSlot;

        /// <summary>0 = X, 1 = Y, 2 = Z for <see cref="PointColorMode.AxisRamp"/>.</summary>
        public int RampAxis = 1;

        public Color FlatColor  = new(0.85f, 0.86f, 0.88f, 1f);
        public Color CloudColor = Color.white;

        public PointSizeMode SizeMode = PointSizeMode.Adaptive;
        public PointShape    Shape    = PointShape.Square;

        /// <summary>Diameter in pixels for FixedPixels, and the clamp target for Adaptive.</summary>
        public float PixelSize = 3f;

        /// <summary>World-space radius. Seeded from the cloud's median nearest-neighbour spacing.</summary>
        public float WorldRadius = 0.01f;

        /// <summary>Pixel clamp for Adaptive sizing. Below 1 px a cloud aliases into holes.</summary>
        public float MinPixelSize = 1f;
        public float MaxPixelSize = 32f;

        /// <summary>
        /// Automatically fall back to the camera-space depth ramp when a cloud carries no
        /// colour, which is requirement #1 of this tool. Called once when a cloud is added.
        /// </summary>
        public void ApplyDefaultsFor(PointCloudDescriptor descriptor)
        {
            ColorMode = descriptor.Has(PointAttributes.Color) ? PointColorMode.Rgb : PointColorMode.ViewDepth;

            if (descriptor.MedianPointSpacing > 0f)
                WorldRadius = descriptor.MedianPointSpacing * 0.75f;
            else if (descriptor.LocalBounds.size.sqrMagnitude > 0f)
                WorldRadius = descriptor.LocalBounds.size.magnitude /
                              Mathf.Max(64f, Mathf.Pow(Mathf.Max(1, descriptor.PointCount), 1f / 3f) * 32f);
        }

        /// <summary>Whether a mode can be shown at all, given what the cloud actually carries.</summary>
        public static bool IsSupported(PointColorMode mode, PointCloudDescriptor descriptor) => mode switch
        {
            PointColorMode.Rgb        => descriptor.Has(PointAttributes.Color),
            PointColorMode.Intensity  => descriptor.Has(PointAttributes.Intensity),
            PointColorMode.Label      => descriptor.Has(PointAttributes.Label),
            PointColorMode.Confidence => descriptor.Has(PointAttributes.Confidence),
            PointColorMode.Timestamp  => descriptor.Has(PointAttributes.Timestamp),
            PointColorMode.NormalRgb  => descriptor.Has(PointAttributes.Normal),
            PointColorMode.Scalar     => (descriptor.Attributes & PointAttributes.AnyScalar) != 0,
            _                         => true,
        };

        /// <summary>Why a mode is unavailable, for the disabled-entry tooltip.</summary>
        public static string UnsupportedReason(PointColorMode mode) => mode switch
        {
            PointColorMode.Rgb        => "This cloud has no per-point colour.",
            PointColorMode.Intensity  => "This cloud has no intensity channel.",
            PointColorMode.Label      => "This cloud has no label/classification channel.",
            PointColorMode.Confidence => "This cloud has no confidence channel.",
            PointColorMode.Timestamp  => "This cloud has no per-point timestamps.",
            PointColorMode.NormalRgb  => "This cloud has no normals.",
            PointColorMode.Scalar     => "This cloud has no generic scalar fields.",
            _                         => null,
        };
    }
}
