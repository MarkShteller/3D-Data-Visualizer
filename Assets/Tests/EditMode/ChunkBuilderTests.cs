using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Synthetic;
using Unity.Collections;
using Unity.Mathematics;

namespace PointCloud.Tests.EditMode
{
    public class ChunkBuilderTests
    {
        const int PointCount = 200_000;
        const int ChunkSize  = 16 * 1024;

        static PointCloudData Generate(SyntheticShape shape = SyntheticShape.SphereShell,
                                       int pointCount = PointCount, uint seed = 12345u)
        {
            var settings = SyntheticCloudSettings.Default(shape, pointCount);
            settings.Seed  = seed;
            settings.Scale = 10f;
            return SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
        }

        [SetUp]
        public void SetUp()
        {
            // Domain reload is disabled in this project, so a leaked native allocation
            // survives into the next play session and slowly eats the editor.
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;
        }

        [Test]
        public void Chunks_TileThePointRangeExactly()
        {
            using var data = Generate();

            Assert.Greater(data.Chunks.Length, 1, "Expected more than one chunk at this point count.");

            int expectedStart = 0;
            long total = 0;
            for (int i = 0; i < data.Chunks.Length; i++)
            {
                var chunk = data.Chunks[i];
                Assert.AreEqual(expectedStart, chunk.Start, $"Chunk {i} does not start where chunk {i - 1} ended.");
                Assert.Greater(chunk.Count, 0, $"Chunk {i} is empty.");
                Assert.AreEqual(chunk.Count, chunk.LodPrefix, $"Chunk {i} should start fully visible.");

                expectedStart = chunk.End;
                total += chunk.Count;
            }

            Assert.AreEqual(data.PointCount, total, "Chunks do not cover every point.");
            Assert.AreEqual(data.PointCount, expectedStart, "Chunk coverage does not end at the last point.");
        }

        [Test]
        public void EveryPointLiesInsideItsChunkBounds()
        {
            using var data = Generate();
            var positions = data.Positions;

            for (int c = 0; c < data.Chunks.Length; c++)
            {
                var chunk = data.Chunks[c];
                for (int i = chunk.Start; i < chunk.End; i++)
                {
                    float3 p = positions[i];
                    // Tight bounds are computed from these exact points, so this is exact
                    // up to float comparison — a failure means the gather and the bounds
                    // computation disagree about which points belong to the chunk.
                    Assert.IsTrue(math.all(p >= chunk.BoundsMin - 1e-4f) &&
                                  math.all(p <= chunk.BoundsMax + 1e-4f),
                        $"Point {i} at {p} is outside chunk {c} bounds " +
                        $"[{chunk.BoundsMin} .. {chunk.BoundsMax}].");
                }
            }
        }

        /// <summary>
        /// The load-bearing invariant: because points are shuffled within a chunk, a short
        /// prefix must still span nearly the whole chunk. If this regresses, progressive
        /// display and movement decimation both start showing spatially biased subsets —
        /// visibly, as a cloud that fills in from one corner.
        /// </summary>
        [Test]
        public void ChunkPrefixIsAnUnbiasedSpatialSample()
        {
            using var data = Generate(SyntheticShape.GaussianBlob);
            var positions = data.Positions;

            for (int c = 0; c < math.min(8, data.Chunks.Length); c++)
            {
                var chunk = data.Chunks[c];
                int prefix = math.max(64, chunk.Count / 20);   // 5% of the chunk

                float3 lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                for (int i = chunk.Start; i < chunk.Start + prefix; i++)
                {
                    lo = math.min(lo, positions[i]);
                    hi = math.max(hi, positions[i]);
                }

                float3 chunkExtent  = math.max(chunk.BoundsMax - chunk.BoundsMin, 1e-6f);
                float3 prefixExtent = hi - lo;
                float3 coverage     = prefixExtent / chunkExtent;

                Assert.Greater(math.cmin(coverage), 0.5f,
                    $"Chunk {c}: a {prefix}-point prefix covers only {math.cmin(coverage):P0} of the " +
                    "chunk's extent. The intra-chunk shuffle is not working.");
            }
        }

        [Test]
        public void BuildIsDeterministicForAGivenSeed()
        {
            using var a = Generate(seed: 777u);
            using var b = Generate(seed: 777u);

            var pa = a.Positions;
            var pb = b.Positions;

            Assert.AreEqual(a.Chunks.Length, b.Chunks.Length);
            for (int i = 0; i < pa.Length; i++)
            {
                if (!math.all(pa[i] == pb[i]))
                    Assert.Fail($"Point {i} differs between two builds with the same seed: {pa[i]} vs {pb[i]}. " +
                                "Render regression tests depend on this being reproducible.");
            }
        }

        [Test]
        public void AllStreamsAreReorderedConsistently()
        {
            using var data = Generate();

            // Colour is generated as a pure function of position, so if the gather permuted
            // streams inconsistently the two will no longer agree.
            var positions = data.Positions;
            var colors    = data.Get(PointAttributes.Color).As<uint>();

            const float scale = 10f;
            for (int i = 0; i < positions.Length; i += 97)
            {
                float3 expected = math.saturate(positions[i] / (scale * 2f) + 0.5f);
                uint packed = colors[i];

                float3 actual = new float3(packed & 0xFFu, (packed >> 8) & 0xFFu, (packed >> 16) & 0xFFu) / 255f;
                Assert.IsTrue(math.all(math.abs(actual - expected) < 0.01f),
                    $"Point {i}: colour {actual} does not match the colour implied by position {positions[i]} " +
                    $"(expected {expected}). Streams were permuted inconsistently.");
            }
        }

        [Test]
        public void FlatCloud_DoesNotDivideByZeroOnTheDegenerateAxis()
        {
            // A single plane is extremely common in CV output and collapses one Morton axis.
            using var data = Generate(SyntheticShape.PlaneGrid, 50_000);

            Assert.Greater(data.Chunks.Length, 0);
            var positions = data.Positions;
            for (int i = 0; i < positions.Length; i += 251)
                Assert.IsFalse(math.any(math.isnan(positions[i])), $"Point {i} is NaN after chunk building.");
        }

        [Test]
        public void SingleChunk_WhenPointCountIsSmall()
        {
            using var data = Generate(SyntheticShape.SphereShell, 100);

            Assert.AreEqual(1, data.Chunks.Length);
            Assert.AreEqual(0, data.Chunks[0].Start);
            Assert.AreEqual(100, data.Chunks[0].Count);
        }
    }
}
