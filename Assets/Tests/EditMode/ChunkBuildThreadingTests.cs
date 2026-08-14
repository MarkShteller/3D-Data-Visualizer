using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Spatial;
using PointCloud.Core.Synthetic;
using Unity.Collections;

namespace PointCloud.Tests.EditMode
{
    /// <summary>
    /// Isolates one variable: does scheduling the chunk-build jobs from a background thread
    /// cost anything?
    ///
    /// It matters because every file load runs on Task.Run — blocking file IO has no
    /// business on a Job System worker — while synthetic generation runs on the main
    /// thread. If the two differ, every measurement taken against synthetic data is
    /// misleading about what a real load costs.
    /// </summary>
    public class ChunkBuildThreadingTests
    {
        const int Points = 2_000_000;

        static PointCloudData Fresh()
        {
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, Points);
            settings.Attributes = PointAttributes.Position | PointAttributes.Color;
            return SyntheticCloudGenerator.Generate(settings, Allocator.Persistent, buildChunks: false);
        }

        /// <summary>
        /// The decisive one: in a fresh process, does the thread that FIRST runs a
        /// CompileSynchronously job determine whether it gets Burst-compiled at all?
        ///
        /// This is not academic. In the real app the first chunk build of the session
        /// happens inside a file load, which runs on Task.Run — so if a background-thread
        /// first touch loses Burst, every load for the rest of the session runs the managed
        /// fallback, and no synthetic benchmark would ever reveal it.
        ///
        /// Deliberately named to sort first, because NUnit runs tests alphabetically within
        /// a class and this must observe a cold Burst state.
        /// </summary>
        [Test]
        public void AAA_FirstTouchFromBackgroundThread_StillGetsBurstCompiled()
        {
            // What AppBootstrap does at startup, and the entire reason it does it. Without
            // this line the background build below takes ~2200 ms instead of ~90 ms.
            PointCloud.Core.JobWarmup.Run();

            double backgroundMs;
            ChunkBuildStats backgroundStats = default;

            using (var data = Fresh())
            {
                var stopwatch = Stopwatch.StartNew();
                Task.Run(() => ChunkBuilder.Build(data, out backgroundStats)).GetAwaiter().GetResult();
                backgroundMs = stopwatch.Elapsed.TotalMilliseconds;
            }

            double mainMs;
            using (var data = Fresh())
            {
                var stopwatch = Stopwatch.StartNew();
                ChunkBuilder.Build(data, out _);
                mainMs = stopwatch.Elapsed.TotalMilliseconds;
            }

            TestContext.WriteLine($"cold first touch on background thread: {backgroundMs:F0} ms  [{backgroundStats}]");
            TestContext.WriteLine($"subsequent main-thread build:          {mainMs:F0} ms");

            Assert.Less(backgroundMs, mainMs * 4.0 + 400.0,
                $"A background-thread build took {backgroundMs:F0} ms versus {mainMs:F0} ms on the " +
                "main thread despite JobWarmup having run. The warm-up is no longer covering " +
                "every job on the load path, so file loads have silently fallen back to managed.");
        }

        /// <summary>
        /// Leak detection with stack traces captures a call stack on every native
        /// allocation. That is exactly what we want during tests, but if it also slows the
        /// jobs themselves then every timing measured under it is fiction — and the whole
        /// suite runs under it.
        /// </summary>
        [Test]
        public void ChunkBuild_CostUnderLeakDetectionModes()
        {
            using (var warm = Fresh()) ChunkBuilder.Build(warm, out _);

            var previous = NativeLeakDetection.Mode;
            try
            {
                foreach (var mode in new[] { NativeLeakDetectionMode.Disabled,
                                             NativeLeakDetectionMode.Enabled,
                                             NativeLeakDetectionMode.EnabledWithStackTrace })
                {
                    NativeLeakDetection.Mode = mode;

                    using var data = Fresh();
                    var stopwatch = Stopwatch.StartNew();
                    ChunkBuilder.Build(data, out var stats);
                    TestContext.WriteLine($"{mode,-24} {stopwatch.Elapsed.TotalMilliseconds,7:F0} ms  [{stats}]");
                }
            }
            finally
            {
                NativeLeakDetection.Mode = previous;
            }
        }

        [Test]
        public void ChunkBuild_CostsTheSameOnMainAndBackgroundThreads()
        {
            // Warm-up so Burst compilation is not attributed to either measurement.
            using (var warm = Fresh()) ChunkBuilder.Build(warm, out _);

            ChunkBuildStats mainStats, backgroundStats;
            double mainMs, backgroundMs;

            using (var data = Fresh())
            {
                var stopwatch = Stopwatch.StartNew();
                ChunkBuilder.Build(data, out mainStats);
                mainMs = stopwatch.Elapsed.TotalMilliseconds;
            }

            using (var data = Fresh())
            {
                var stopwatch = Stopwatch.StartNew();
                ChunkBuildStats captured = default;
                Task.Run(() => ChunkBuilder.Build(data, out captured)).GetAwaiter().GetResult();
                backgroundMs = stopwatch.Elapsed.TotalMilliseconds;
                backgroundStats = captured;
            }

            TestContext.WriteLine($"main thread:       {mainMs:F0} ms  [{mainStats}]");
            TestContext.WriteLine($"background thread: {backgroundMs:F0} ms  [{backgroundStats}]");
            TestContext.WriteLine($"ratio: {backgroundMs / mainMs:F1}x");

            // Some overhead is expected; an order of magnitude is a defect, and it would make
            // every real file load pay it.
            Assert.Less(backgroundMs, mainMs * 3.0 + 50.0,
                $"Chunk building takes {backgroundMs / mainMs:F1}x longer when scheduled from a " +
                "background thread. Every file load pays this, and no synthetic benchmark shows it.");
        }
    }
}
