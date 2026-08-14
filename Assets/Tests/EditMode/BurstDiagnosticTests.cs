using System.Diagnostics;
using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Spatial;
using PointCloud.Core.Synthetic;
using Unity.Burst;
using Unity.Collections;

namespace PointCloud.Tests.EditMode
{
    /// <summary>
    /// Where does load time actually go?
    ///
    /// This matters far beyond test runtime: generation and chunk building run once per
    /// load over the entire cloud, so a slow stage is a multi-minute freeze for a user
    /// opening a large file. Broken out per stage because wall-clock guessing about which
    /// part was slow already cost real time twice.
    /// </summary>
    public class BurstDiagnosticTests
    {
        const int Points = 2_000_000;

        static SyntheticCloudSettings Settings(int points) =>
            new()
            {
                Shape      = SyntheticShape.SphereShell,
                PointCount = points,
                Attributes = PointAttributes.Position | PointAttributes.Color,
                Seed       = 12345u,
                Scale      = 10f,
                LabelCount = 8,
            };

        [Test]
        public void ReportBurstStatusAndPerStageThroughput()
        {
            TestContext.WriteLine(
                $"Burst enabled={BurstCompiler.IsEnabled}, " +
                $"EnableBurstCompilation={BurstCompiler.Options.EnableBurstCompilation}, " +
                $"EnableBurstCompileSynchronously={BurstCompiler.Options.EnableBurstCompileSynchronously}, " +
                $"leakDetection={NativeLeakDetection.Mode}, " +
                $"workerCount={Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount}");

            // Warm-up at a trivial size so synchronous Burst compilation is paid here rather
            // than being attributed to a stage below.
            SyntheticCloudGenerator.Generate(Settings(1024), Allocator.Persistent).Dispose();

            var stopwatch = Stopwatch.StartNew();
            var data = SyntheticCloudGenerator.Generate(Settings(Points), Allocator.Persistent, buildChunks: false);
            double generateMs = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            ChunkBuilder.Build(data, out var chunkStats, ChunkBuilder.DefaultChunkSize, 12345u, Allocator.Persistent);
            double chunkMs = stopwatch.Elapsed.TotalMilliseconds;

            using (data)
            {
                double perMillion = (generateMs + chunkMs) / (Points / 1_000_000.0);

                TestContext.WriteLine(
                    $"{Points:N0} points — generate {generateMs:F0} ms, chunk build {chunkMs:F0} ms, " +
                    $"total {generateMs + chunkMs:F0} ms ({perMillion:F0} ms/million). " +
                    $"Extrapolated to 20M: {perMillion * 20 / 1000.0:F1} s.");
                TestContext.WriteLine($"  chunk stages: {chunkStats}");

                Assert.AreEqual(Points, data.PointCount);
                Assert.Greater(data.Chunks.Length, 0);

                // Burst-accelerated, both stages land well under a second per million.
                // Generous bounds so this reports on slow hardware rather than flaking, while
                // still catching a total loss of acceleration.
                Assert.Less(generateMs / (Points / 1_000_000.0), 1500.0,
                    "Generation is not being accelerated.");
                Assert.Less(chunkMs / (Points / 1_000_000.0), 2000.0,
                    "Chunk building is not being accelerated.");
            }
        }
    }
}
