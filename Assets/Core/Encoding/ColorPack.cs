using Unity.Burst;
using Unity.Mathematics;

namespace PointCloud.Core.Encoding
{
    /// <summary>
    /// RGBA8 packed into a uint32, laid out so that the four bytes in memory are r, g, b, a
    /// in that order on a little-endian machine.
    ///
    /// That layout is not arbitrary: PLY and PCD store colour as consecutive bytes in
    /// exactly this order, so a parser can blit them straight into the stream and the GPU
    /// unpack still lines up. No byte shuffling on the hot path.
    ///
    /// MUST stay bit-identical to UnpackRGBA8() in PointCloudCommon.hlsl.
    ///
    /// Note these bytes are NOT converted to linear here. Conversion happens in the shader,
    /// gated on Descriptor.ColorIsLinear, so the inspector can still report the byte-exact
    /// source value — which is what someone debugging a colorization pipeline needs to see.
    /// </summary>
    [BurstCompile]
    public static class ColorPack
    {
        public static uint FromBytes(byte r, byte g, byte b, byte a = 255) =>
            r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

        public static void ToBytes(uint packed, out byte r, out byte g, out byte b, out byte a)
        {
            r = (byte)(packed & 0xFFu);
            g = (byte)((packed >> 8) & 0xFFu);
            b = (byte)((packed >> 16) & 0xFFu);
            a = (byte)((packed >> 24) & 0xFFu);
        }

        /// <summary>Pack from 0..1 floats. Used for float-valued source colours and generators.</summary>
        public static uint FromFloat4(float4 rgba)
        {
            uint4 q = (uint4)math.round(math.saturate(rgba) * 255f);
            return q.x | (q.y << 8) | (q.z << 16) | (q.w << 24);
        }

        public static uint FromFloat3(float3 rgb) => FromFloat4(new float4(rgb, 1f));

        /// <summary>Unpack to 0..1 floats without any transfer-function conversion.</summary>
        public static float4 ToFloat4(uint packed) =>
            new float4(packed & 0xFFu, (packed >> 8) & 0xFFu, (packed >> 16) & 0xFFu, (packed >> 24) & 0xFFu)
            * (1f / 255f);

        /// <summary>
        /// Exact sRGB EOTF, matching Color.hlsl's SRGBToLinear. Only for CPU-side work such
        /// as computing a mean colour; the render path converts on the GPU.
        /// </summary>
        public static float SrgbToLinear(float c) =>
            c <= 0.04045f ? c / 12.92f : math.pow((c + 0.055f) / 1.055f, 2.4f);

        public static float LinearToSrgb(float c) =>
            c <= 0.0031308f ? c * 12.92f : 1.055f * math.pow(c, 1f / 2.4f) - 0.055f;

        public static float3 SrgbToLinear(float3 c) =>
            new float3(SrgbToLinear(c.x), SrgbToLinear(c.y), SrgbToLinear(c.z));
    }
}
