using PointCloud.Core.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace PointCloud.Core.Spatial
{
    /// <summary>
    /// Reorders a loaded cloud into spatially-local chunks and builds the chunk table.
    ///
    /// Pipeline: AABB -> Morton key per point -> sort -> slice into fixed-size runs ->
    /// shuffle within each run -> gather every attribute stream through the permutation.
    ///
    /// The shuffle is the step that matters. After it, each chunk is still spatially tight
    /// (so frustum culling works) but any prefix of a chunk is an unbiased uniform sample
    /// of it. Progressive display, movement decimation and coverage-based LOD then all
    /// reduce to picking a prefix length, with no octree and no second data layout.
    ///
    /// Every job here is CompileSynchronously. Burst compiles asynchronously by default,
    /// and these jobs run exactly once per load over the entire cloud — so the async path
    /// means the one execution that matters always runs unaccelerated. Measured in the
    /// editor at 20M points that is the difference between about a second and minutes of a
    /// single pegged core.
    /// </summary>
    /// <summary>
    /// Per-stage timings for one chunk build. Surfaced in the load log, because when a
    /// large file takes a while to open the useful question is always "doing what?".
    /// </summary>
    public struct ChunkBuildStats
    {
        public double BoundsMs, MortonMs, SortMs, ChunksMs, GatherMs;

        public double TotalMs => BoundsMs + MortonMs + SortMs + ChunksMs + GatherMs;

        public override string ToString() =>
            $"bounds {BoundsMs:F0} ms, morton {MortonMs:F0} ms, sort {SortMs:F0} ms, " +
            $"chunks {ChunksMs:F0} ms, gather {GatherMs:F0} ms (total {TotalMs:F0} ms)";
    }

    public static class ChunkBuilder
    {
        /// <summary>
        /// 128 K points per chunk: ~157 chunks for a 20M cloud. Fine enough that frustum
        /// culling actually rejects work, coarse enough that the draw-command list stays
        /// trivial, and each position slab is 1.5 MB — a natural upload slice.
        /// </summary>
        public const int DefaultChunkSize = 128 * 1024;

        /// <summary>
        /// Reorder <paramref name="data"/> in place and assign its Chunks table.
        /// Also fills in the descriptor's LocalBounds.
        /// </summary>
        public static void Build(PointCloudData data,
                                 int chunkSize = DefaultChunkSize,
                                 uint seed = 0x9E3779B9u,
                                 Allocator allocator = Allocator.Persistent)
            => Build(data, out _, chunkSize, seed, allocator);

        /// <summary>Build, reporting per-stage timings.</summary>
        public static void Build(PointCloudData data,
                                 out ChunkBuildStats stats,
                                 int chunkSize = DefaultChunkSize,
                                 uint seed = 0x9E3779B9u,
                                 Allocator allocator = Allocator.Persistent)
        {
            stats = default;

            int count = data.PointCount;
            if (count <= 0)
            {
                data.Chunks = new NativeArray<PointChunk>(0, allocator);
                return;
            }

            chunkSize = math.max(1, chunkSize);
            var positions = data.Positions;
            var timer = System.Diagnostics.Stopwatch.StartNew();

            // 1. AABB of the whole cloud.
            var boundsResult = new NativeArray<float3>(2, Allocator.TempJob);
            new ComputeBoundsJob { Positions = positions, Result = boundsResult }.Schedule().Complete();

            float3 boundsMin = boundsResult[0];
            float3 boundsMax = boundsResult[1];
            boundsResult.Dispose();
            stats.BoundsMs = timer.Elapsed.TotalMilliseconds; timer.Restart();

            // 2. Morton key per point, packed with the point index.
            var keys = new NativeArray<ulong>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new MortonKeyJob
            {
                Positions  = positions,
                BoundsMin  = boundsMin,
                InvExtent  = MortonCode.InverseExtent(boundsMin, boundsMax),
                Keys       = keys,
            }.Schedule(count, 4096).Complete();
            stats.MortonMs = timer.Elapsed.TotalMilliseconds; timer.Restart();

            // 3. Sort by Morton code, which orders points by locality and leaves the original
            //    index recoverable from the low bits.
            //
            // A hand-written radix sort rather than NativeArray.SortJob(): that one is
            // generic, and Burst does not compile generic jobs in the editor, so it ran
            // managed. Measured at 2M points it took 9117 ms of a 9149 ms chunk build —
            // 99.6% of the cost — while every job here compiled and ran in single-digit
            // milliseconds. Radix is also O(n) rather than O(n log n) and needs only the
            // 30-bit Morton code, not the whole key.
            var scratch = new NativeArray<ulong>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            new RadixSortMortonJob { Keys = keys, Scratch = scratch }.Schedule().Complete();
            scratch.Dispose();
            stats.SortMs = timer.Elapsed.TotalMilliseconds; timer.Restart();

            data.Descriptor.LocalBounds = new Bounds(
                (boundsMin + boundsMax) * 0.5f,
                math.max(boundsMax - boundsMin, 0f));

            // 4. Chunk table, plus the shuffle that makes any prefix a uniform sample.
            int chunkCount = (count + chunkSize - 1) / chunkSize;
            var chunks = new NativeArray<PointChunk>(chunkCount, allocator, NativeArrayOptions.UninitializedMemory);

            var permutation = new NativeArray<int>(count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var chunkHandle = new BuildChunksJob
            {
                Keys        = keys,
                Positions   = positions,
                PointCount  = count,
                ChunkSize   = chunkSize,
                Seed        = seed == 0u ? 1u : seed,
                Chunks      = chunks,
                Permutation = permutation,
            }.Schedule(chunkCount, 1);

            chunkHandle.Complete();
            keys.Dispose();
            stats.ChunksMs = timer.Elapsed.TotalMilliseconds; timer.Restart();

            // 5. Gather every stream through the permutation.
            var gatherHandles = new NativeList<JobHandle>(PointAttributeInfo.SlotCount, Allocator.Temp);
            var replacements  = new AttributeStream[PointAttributeInfo.SlotCount];

            var streams = data.StreamsInternal;
            for (int slot = 0; slot < streams.Length; slot++)
            {
                if (!streams[slot].IsCreated) continue;

                var attribute   = (PointAttributes)(1u << slot);
                var destination = AttributeStream.Allocate(attribute, count, allocator);
                replacements[slot] = destination;

                gatherHandles.Add(new GatherBytesJob
                {
                    Permutation = permutation,
                    Source      = streams[slot].Data,
                    Destination = destination.Data,
                    ElementSize = destination.ElementSize,
                }.Schedule(count, 8192));
            }

            JobHandle.CompleteAll(gatherHandles.AsArray());
            gatherHandles.Dispose();
            permutation.Dispose();

            for (int slot = 0; slot < replacements.Length; slot++)
            {
                if (!replacements[slot].IsCreated) continue;
                data.ReplaceStream((PointAttributes)(1u << slot), replacements[slot]);
            }

            data.Chunks = chunks;
            stats.GatherMs = timer.Elapsed.TotalMilliseconds;
        }

        [BurstCompile(CompileSynchronously = true)]
        struct ComputeBoundsJob : IJob
        {
            [ReadOnly] public NativeArray<float3> Positions;
            [WriteOnly] public NativeArray<float3> Result;   // [0] = min, [1] = max

            public void Execute()
            {
                float3 lo = float.PositiveInfinity;
                float3 hi = float.NegativeInfinity;

                for (int i = 0; i < Positions.Length; i++)
                {
                    float3 p = Positions[i];
                    // NaN-safe: comparisons with NaN are false, so min/max would propagate it
                    // through the whole AABB and take every Morton code with it.
                    if (math.any(math.isnan(p))) continue;
                    lo = math.min(lo, p);
                    hi = math.max(hi, p);
                }

                if (math.any(lo > hi)) { lo = 0f; hi = 0f; }   // every point was NaN

                Result[0] = lo;
                Result[1] = hi;
            }
        }

        /// <summary>
        /// LSD radix sort of the packed keys by their 30-bit Morton code: four passes of
        /// eight bits, ping-ponging between the key array and scratch.
        ///
        /// Four passes is deliberately even, so the sorted result lands back in
        /// <see cref="Keys"/> with no final copy. Only the Morton code is used as the sort
        /// key — the point index in the low bits is along for the ride, and ties among
        /// equal Morton codes need no particular order because points get shuffled within
        /// their chunk immediately afterwards anyway.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        struct RadixSortMortonJob : IJob
        {
            public NativeArray<ulong> Keys;
            public NativeArray<ulong> Scratch;

            public void Execute()
            {
                int n = Keys.Length;
                if (n < 2) return;

                var counts  = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                var offsets = new NativeArray<int>(256, Allocator.Temp, NativeArrayOptions.UninitializedMemory);

                for (int pass = 0; pass < 4; pass++)
                {
                    int shift = pass * 8;
                    bool evenPass = (pass & 1) == 0;

                    // Aliases rather than copies: NativeArray is a handle, so this just
                    // selects which buffer is source and which is destination this pass.
                    var source      = evenPass ? Keys : Scratch;
                    var destination = evenPass ? Scratch : Keys;

                    for (int i = 0; i < 256; i++) counts[i] = 0;

                    for (int i = 0; i < n; i++)
                    {
                        uint morton = (uint)(source[i] >> MortonCode.IndexBits);
                        counts[(int)((morton >> shift) & 0xFFu)]++;
                    }

                    int running = 0;
                    for (int bucket = 0; bucket < 256; bucket++)
                    {
                        offsets[bucket] = running;
                        running += counts[bucket];
                    }

                    for (int i = 0; i < n; i++)
                    {
                        ulong key = source[i];
                        uint morton = (uint)(key >> MortonCode.IndexBits);
                        int bucket = (int)((morton >> shift) & 0xFFu);
                        destination[offsets[bucket]++] = key;
                    }
                }

                counts.Dispose();
                offsets.Dispose();
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct MortonKeyJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Positions;
            public float3 BoundsMin;
            public float3 InvExtent;
            [WriteOnly] public NativeArray<ulong> Keys;

            public void Execute(int i)
            {
                float3 p = Positions[i];
                if (math.any(math.isnan(p))) p = BoundsMin;

                Keys[i] = MortonCode.Key(MortonCode.Encode(MortonCode.Quantize(p, BoundsMin, InvExtent)), i);
            }
        }

        /// <summary>
        /// One chunk per iteration: unpack the sorted keys into the permutation, compute the
        /// chunk's tight AABB, then Fisher-Yates shuffle the chunk's own slice.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        struct BuildChunksJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<ulong> Keys;
            [ReadOnly] public NativeArray<float3> Positions;
            public int  PointCount;
            public int  ChunkSize;
            public uint Seed;

            [WriteOnly] public NativeArray<PointChunk> Chunks;

            // Each iteration owns a disjoint [start, end) slice, which the parallel-for
            // safety system cannot infer from the index alone.
            [NativeDisableParallelForRestriction] public NativeArray<int> Permutation;

            public void Execute(int chunkIndex)
            {
                int start = chunkIndex * ChunkSize;
                int end   = math.min(start + ChunkSize, PointCount);
                int count = end - start;

                float3 lo = float.PositiveInfinity;
                float3 hi = float.NegativeInfinity;

                for (int i = start; i < end; i++)
                {
                    int index = MortonCode.IndexOf(Keys[i]);
                    Permutation[i] = index;

                    float3 p = Positions[index];
                    if (math.any(math.isnan(p))) continue;
                    lo = math.min(lo, p);
                    hi = math.max(hi, p);
                }

                if (math.any(lo > hi)) { lo = 0f; hi = 0f; }

                // Seeded per chunk so the result is deterministic and reproducible — a render
                // regression test needs the same cloud to produce the same pixels every run.
                var rng = new Random(math.hash(new uint2(Seed, (uint)chunkIndex + 1u)) | 1u);
                for (int i = count - 1; i > 0; i--)
                {
                    int j = rng.NextInt(0, i + 1);
                    (Permutation[start + i], Permutation[start + j]) =
                        (Permutation[start + j], Permutation[start + i]);
                }

                Chunks[chunkIndex] = new PointChunk
                {
                    Start     = start,
                    Count     = count,
                    BoundsMin = lo,
                    BoundsMax = hi,
                    LodPrefix = count,
                };
            }
        }

        /// <summary>Reorder a byte stream of fixed-size elements through a permutation.</summary>
        [BurstCompile(CompileSynchronously = true)]
        unsafe struct GatherBytesJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> Permutation;
            [ReadOnly] public NativeArray<byte> Source;
            [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<byte> Destination;
            public int ElementSize;

            public void Execute(int i)
            {
                byte* src = (byte*)Source.GetUnsafeReadOnlyPtr() + (long)Permutation[i] * ElementSize;
                byte* dst = (byte*)Destination.GetUnsafePtr() + (long)i * ElementSize;
                UnsafeUtility.MemCpy(dst, src, ElementSize);
            }
        }
    }
}
