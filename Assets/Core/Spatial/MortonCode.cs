using Unity.Burst;
using Unity.Mathematics;

namespace PointCloud.Core.Spatial
{
    /// <summary>
    /// 30-bit Morton (Z-order) codes: 10 bits per axis, a 1024^3 lattice.
    ///
    /// 10 bits is deliberate rather than the usual 21. The code only has to order points
    /// well enough that a run of 128 K consecutive points is spatially tight — chunk
    /// granularity, not point granularity. 10 bits leaves 34 bits in a ulong for the point
    /// index, so a single key sort produces the permutation directly with no second array
    /// and no indirection during the sort.
    /// </summary>
    [BurstCompile]
    public static class MortonCode
    {
        public const int  BitsPerAxis = 10;
        public const uint AxisMax     = (1u << BitsPerAxis) - 1u;   // 1023

        /// <summary>Bits available for the point index in a packed sort key.</summary>
        public const int  IndexBits = 34;
        public const ulong IndexMask = (1UL << IndexBits) - 1UL;

        /// <summary>Spread the low 10 bits of x so each occupies every third bit position.</summary>
        public static uint Part1By2(uint x)
        {
            x &= 0x000003FFu;
            x = (x ^ (x << 16)) & 0xFF0000FFu;
            x = (x ^ (x << 8))  & 0x0300F00Fu;
            x = (x ^ (x << 4))  & 0x030C30C3u;
            x = (x ^ (x << 2))  & 0x09249249u;
            return x;
        }

        /// <summary>Interleave three 10-bit lattice coordinates into a 30-bit code.</summary>
        public static uint Encode(uint3 v) =>
            Part1By2(v.x) | (Part1By2(v.y) << 1) | (Part1By2(v.z) << 2);

        /// <summary>
        /// Quantise a position into the lattice. <paramref name="invExtent"/> is
        /// <c>1 / (boundsMax - boundsMin)</c> with zero-extent axes pre-collapsed to 0,
        /// so a perfectly flat cloud (a single plane, very common in CV output) does not
        /// divide by zero and simply puts every point on lattice row 0 of that axis.
        /// </summary>
        public static uint3 Quantize(float3 position, float3 boundsMin, float3 invExtent)
        {
            float3 t = math.saturate((position - boundsMin) * invExtent);
            return (uint3)math.min(t * AxisMax, AxisMax);
        }

        /// <summary>Pack a Morton code and a point index into one sortable key.</summary>
        public static ulong Key(uint morton, int index) =>
            ((ulong)morton << IndexBits) | ((ulong)(uint)index & IndexMask);

        public static int IndexOf(ulong key) => (int)(key & IndexMask);

        /// <summary>Reciprocal extent with degenerate axes collapsed. Pair with <see cref="Quantize"/>.</summary>
        public static float3 InverseExtent(float3 boundsMin, float3 boundsMax)
        {
            float3 extent = boundsMax - boundsMin;
            return math.select(1f / extent, 0f, extent <= 1e-20f);
        }
    }
}
