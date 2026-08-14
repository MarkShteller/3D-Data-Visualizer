using Unity.Burst;
using Unity.Mathematics;

namespace PointCloud.Core.Encoding
{
    /// <summary>
    /// Octahedral encoding of unit vectors into a single uint32 (two 16-bit channels).
    /// 12 B/point becomes 4 B/point — 240 MB saved on a 20M-point cloud — at a worst-case
    /// error well under a tenth of a degree, which is invisible for normal-direction
    /// colouring and splat orientation.
    ///
    /// MUST stay bit-identical to OctDecode() in PointCloudCommon.hlsl. The mirroring fold
    /// and the sign convention are the two places that silently diverge, so both sides use
    /// the same SignNotZero (HLSL's sign() returns 0 at 0, which folds the -Z hemisphere
    /// onto a seam and produces a visible cross artefact).
    /// </summary>
    [BurstCompile]
    public static class OctNormal
    {
        /// <summary>sign(), but never zero. Matches the HLSL helper of the same name.</summary>
        public static float2 SignNotZero(float2 v) =>
            new float2(v.x >= 0f ? 1f : -1f, v.y >= 0f ? 1f : -1f);

        /// <summary>Encode a (not necessarily normalised) direction into a packed uint32.</summary>
        public static uint Encode(float3 n)
        {
            float l1 = math.abs(n.x) + math.abs(n.y) + math.abs(n.z);
            // A zero-length normal has no direction to preserve; +Z is as good as anything
            // and keeps the encode total rather than throwing deep inside a parse job.
            if (l1 < 1e-20f) return EncodeUnitSquare(new float2(0.5f, 0.5f));

            float3 p = n / l1;
            float2 oct = p.z >= 0f
                ? p.xy
                : (1f - math.abs(new float2(p.y, p.x))) * SignNotZero(p.xy);

            return EncodeUnitSquare(oct * 0.5f + 0.5f);
        }

        /// <summary>Decode a packed uint32 back to a unit vector.</summary>
        public static float3 Decode(uint packed)
        {
            float2 e = DecodeUnitSquare(packed) * 2f - 1f;

            float3 v = new float3(e.x, e.y, 1f - math.abs(e.x) - math.abs(e.y));
            if (v.z < 0f)
            {
                float2 xy = (1f - math.abs(new float2(v.y, v.x))) * SignNotZero(v.xy);
                v.x = xy.x;
                v.y = xy.y;
            }
            return math.normalize(v);
        }

        static uint EncodeUnitSquare(float2 uv)
        {
            uint x = (uint)math.round(math.saturate(uv.x) * 65535f);
            uint y = (uint)math.round(math.saturate(uv.y) * 65535f);
            return x | (y << 16);
        }

        static float2 DecodeUnitSquare(uint packed) =>
            new float2(packed & 0xFFFFu, (packed >> 16) & 0xFFFFu) * (1f / 65535f);
    }
}
