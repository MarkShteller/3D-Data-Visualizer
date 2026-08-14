using PointCloud.Core.Data;
using PointCloud.Core.Synthetic;
using Unity.Collections;

namespace PointCloud.Core
{
    /// <summary>
    /// Forces Burst to compile the bulk jobs, on the main thread, before anything needs them.
    ///
    /// This exists because of a measured and thoroughly non-obvious behaviour: a job whose
    /// FIRST invocation happens on a plain background thread does not get Burst-compiled at
    /// all — it silently runs the managed fallback, and stays that way. Measured on a 2M
    /// point cloud, a cold first touch from Task.Run took 2191 ms against 84 ms once
    /// compiled, a 26x difference with no warning anywhere.
    ///
    /// That is exactly the shape of this application: file loading runs on Task.Run, because
    /// blocking IO has no business on a Job System worker, so the first chunk build of every
    /// session would otherwise be a background-thread first touch and every load for the
    /// rest of the session would pay for it. Synthetic benchmarks never show it, because
    /// generation runs on the main thread and compiles the jobs as a side effect.
    ///
    /// Cost is a few milliseconds on a handful of points.
    /// </summary>
    public static class JobWarmup
    {
        /// <summary>Points used for the warm-up. Enough to be valid, small enough to be free.</summary>
        const int WarmupPoints = 256;

        /// <summary>
        /// Compile the generation and chunk-building jobs. MUST be called from the main
        /// thread — calling it from a background thread defeats the entire purpose.
        /// </summary>
        public static void Run()
        {
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, WarmupPoints);

            // Every attribute, so the gather job is compiled for each stream it will meet.
            settings.Attributes = PointAttributes.Position | PointAttributes.Color |
                                  PointAttributes.Normal | PointAttributes.Intensity |
                                  PointAttributes.Label | PointAttributes.Confidence |
                                  PointAttributes.Timestamp | PointAttributes.Scalar0;

            // Generating with chunks builds the whole pipeline: generate, bounds, Morton,
            // radix sort, chunk slicing and the permutation gather.
            using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
        }
    }
}
