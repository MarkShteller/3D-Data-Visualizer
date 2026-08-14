using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace PointCloud.Core.Data
{
    /// <summary>
    /// A contiguous, spatially-local run of points. The unit of frustum culling, draw
    /// command generation, and level of detail.
    ///
    /// The important invariant: points in [Start, Start+Count) are RANDOMLY SHUFFLED
    /// within the chunk. Spatial locality lives at chunk granularity (the chunk's AABB is
    /// tight, so culling works); sample uniformity lives at prefix granularity (any prefix
    /// [Start, Start+k) is an unbiased uniform sample of the chunk).
    ///
    /// That single property buys three features for free: progressive display while a file
    /// is still uploading, decimation while the camera moves, and a first-cut LOD — all of
    /// them just <c>LodPrefix = Count / k</c>. It is also the seam an octree drops into
    /// later without touching the renderer.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PointChunk
    {
        /// <summary>Index of this chunk's first point in the attribute streams.</summary>
        public int Start;

        /// <summary>Number of points in this chunk.</summary>
        public int Count;

        public float3 BoundsMin;
        public float3 BoundsMax;

        /// <summary>
        /// How many of this chunk's points to draw this frame, in [0, Count]. Recomputed
        /// per frame by the culler from screen coverage and camera motion.
        /// </summary>
        public int LodPrefix;

        public float3 Center => (BoundsMin + BoundsMax) * 0.5f;

        public float3 Extents => (BoundsMax - BoundsMin) * 0.5f;

        /// <summary>Bounding sphere radius. Used for the cheap screen-coverage estimate.</summary>
        public float Radius => math.length(BoundsMax - BoundsMin) * 0.5f;

        public int End => Start + Count;

        public override string ToString() =>
            $"[{Start}..{End}) n={Count} lod={LodPrefix} c={Center} r={Radius:F3}";
    }
}
