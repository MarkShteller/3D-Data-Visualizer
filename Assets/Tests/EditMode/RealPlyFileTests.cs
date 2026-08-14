using System.IO;
using System.Threading;
using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Diagnostics;
using PointCloud.Core.Sources;
using PointCloud.Formats.Ply;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace PointCloud.Tests.EditMode
{
    /// <summary>
    /// Validates against a real exporter's output rather than fixtures this project wrote.
    ///
    /// Hand-built fixtures only prove the parser agrees with itself. A genuine CloudCompare
    /// export exercises the things a synthetic file never will: the obj_info line, a
    /// scalar_ prefixed custom property, real float coordinates, and a header whose exact
    /// byte length has to be right or every record after it is misaligned.
    /// </summary>
    public class RealPlyFileTests
    {
        SourceRegistry _registry;
        PointCloudLoader _loader;
        LoadLog _log;
        string _path;

        [SetUp]
        public void SetUp()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;

            // What AppBootstrap does at startup. Without it, the decode and chunk jobs are
            // first touched on the loader's background thread and never get Burst-compiled,
            // which costs roughly 25x on every load.
            PointCloud.Core.JobWarmup.Run();
            PointCloud.Formats.FormatJobWarmup.Run();

            _log = new LoadLog();
            _registry = new SourceRegistry();
            _registry.Register(new PlySourceFactory(_log));
            _loader = new PointCloudLoader(_registry, _log);

            var directory = Path.Combine(Application.dataPath, "Resources");
            _path = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.ply", SearchOption.TopDirectoryOnly).FirstOrDefaultOrNull()
                : null;

            if (_path == null)
                Assert.Ignore("No .ply in Assets/Resources — this validation needs a real sample file.");
        }

        LoadResult Load(FrameRequest? request = null) =>
            _loader.LoadAsync(_path, request ?? FrameRequest.Default, null, CancellationToken.None)
                   .GetAwaiter().GetResult();

        [Test]
        public void RealCloudCompareExport_LoadsAndIsInternallyConsistent()
        {
            var fileInfo = new FileInfo(_path);
            var result = Load();

            if (!result.Succeeded)
                Assert.Fail($"Failed to load {fileInfo.Name}: {result.UserMessage}\n{result.Error}");

            using var frame = result.Frame;
            var data = frame.Data;
            var descriptor = data.Descriptor;

            TestContext.WriteLine(
                $"{fileInfo.Name}  {fileInfo.Length / (1024 * 1024)} MB\n" +
                $"  {descriptor}\n" +
                $"  loaded in {result.ElapsedMs:F0} ms " +
                $"({descriptor.PointCount / math.max(1.0, result.ElapsedMs) * 1000.0 / 1e6:F1} M points/s)\n" +
                $"  bounds centre {descriptor.LocalBounds.center} size {descriptor.LocalBounds.size}\n" +
                $"  origin offset {descriptor.OriginOffset}\n" +
                $"  spacing estimate {descriptor.MedianPointSpacing:F4}\n" +
                $"  chunks {data.Chunks.Length}, colour linear = {descriptor.ColorIsLinear}");

            foreach (var field in descriptor.ScalarFields)
                TestContext.WriteLine($"  scalar: {field}");

            // --- header agreement -------------------------------------------------
            // The declared count and the stride must together account for the file, or the
            // header length was misread and every record is shifted.
            Assert.Greater(descriptor.PointCount, 0);
            Assert.IsTrue(descriptor.Has(PointAttributes.Position));

            // --- positions --------------------------------------------------------
            var positions = data.Positions;
            Assert.AreEqual(descriptor.PointCount, positions.Length);

            int nonFinite = 0;
            float3 lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            for (int i = 0; i < positions.Length; i++)
            {
                float3 p = positions[i];
                if (math.any(math.isnan(p)) || math.any(math.isinf(p))) { nonFinite++; continue; }
                lo = math.min(lo, p);
                hi = math.max(hi, p);
            }

            Assert.AreEqual(0, nonFinite,
                $"{nonFinite:N0} positions are NaN or infinite — the record stride is probably wrong.");

            float3 size = hi - lo;
            Assert.Greater(math.cmax(size), 0f, "The cloud has zero extent in every axis.");

            // A misaligned stride reads colour bytes as float exponents, which produces
            // wildly asymmetric extents spanning many orders of magnitude.
            float widest = math.cmax(size), narrowest = math.cmax(math.min(size, 1e30f));
            Assert.Less(widest, 1e12f,
                $"Bounds span {widest:E2} units, which indicates records are being read misaligned.");

            // --- bounds agree with the chunk table ---------------------------------
            var declared = descriptor.LocalBounds;
            Assert.That(declared.min.x, Is.EqualTo(lo.x).Within(1e-3f), "Chunk-built bounds disagree with the data.");
            Assert.That(declared.max.x, Is.EqualTo(hi.x).Within(1e-3f));

            long chunkTotal = 0;
            for (int i = 0; i < data.Chunks.Length; i++)
            {
                var chunk = data.Chunks[i];
                chunkTotal += chunk.Count;
                Assert.IsTrue(math.all(chunk.BoundsMin <= chunk.BoundsMax),
                    $"Chunk {i} has inverted bounds.");
            }
            Assert.AreEqual(descriptor.PointCount, chunkTotal, "Chunks do not cover every point.");

            // --- colour -----------------------------------------------------------
            if (descriptor.Has(PointAttributes.Color))
            {
                Assert.IsFalse(descriptor.ColorIsLinear,
                    "uchar colour is sRGB; flagging it linear would render this file washed out.");

                var colors = data.Get(PointAttributes.Color).As<uint>();
                long sum = 0;
                int opaque = 0;
                for (int i = 0; i < colors.Length; i += 97)
                {
                    uint packed = colors[i];
                    sum += (packed & 0xFF) + ((packed >> 8) & 0xFF) + ((packed >> 16) & 0xFF);
                    if (((packed >> 24) & 0xFF) == 255) opaque++;
                    else Assert.Fail($"Point {i} has alpha {(packed >> 24) & 0xFF}; " +
                                     "a file with no alpha property must default to opaque.");
                }

                int sampled = (colors.Length + 96) / 97;
                double meanChannel = sum / (3.0 * sampled);
                Assert.Greater(meanChannel, 1.0,
                    "Every sampled colour is near black, which usually means the colour offset is wrong.");
                Assert.Less(meanChannel, 254.0, "Every sampled colour is saturated white.");
                TestContext.WriteLine($"  mean colour channel {meanChannel:F1}/255 over {sampled:N0} samples");
            }
        }

        /// <summary>
        /// The scalar field is what makes this file interesting: an engineer's custom
        /// per-point value must survive with its name intact, not be silently dropped.
        /// </summary>
        [Test]
        public void CustomScalarProperty_SurvivesWithItsSourceName()
        {
            var result = Load();
            Assert.IsTrue(result.Succeeded, result.UserMessage);

            using var frame = result.Frame;
            var descriptor = frame.Data.Descriptor;

            if (descriptor.ScalarFields.Length == 0)
            {
                TestContext.WriteLine("This file declares no custom scalar properties; nothing to check.");
                return;
            }

            var field = descriptor.ScalarFields[0];
            Assert.IsNotEmpty(field.Name);
            StringAssert.StartsWith("scalar_", field.Name,
                "CloudCompare prefixes its scalar exports, and the verbatim source name is " +
                "what the engineer will recognise in the UI.");

            Assert.IsTrue(descriptor.Has(PointAttributes.Scalar0));
            Assert.Less(field.SourceRange.x, field.SourceRange.y + 1e-9f,
                "The observed scalar range is inverted or empty.");

            var values = frame.Data.Get(PointAttributes.Scalar0).As<uint>();
            int finite = 0;
            for (int i = 0; i < values.Length; i += 101)
                if (!float.IsNaN(math.asfloat(values[i]))) finite++;

            Assert.Greater(finite, 0, "Every sampled scalar value is NaN.");
            TestContext.WriteLine($"  scalar '{field.Name}' range [{field.SourceRange.x}, {field.SourceRange.y}]");
        }

        /// <summary>
        /// Separates one-time cost from steady-state throughput.
        ///
        /// The decode jobs are CompileSynchronously, so the first load in a fresh process
        /// pays for Burst compiling them. That is the right trade — the alternative is the
        /// managed fallback, which was measured at a hundredfold worse — but it means a
        /// single cold measurement says nothing about how fast loading actually is.
        /// </summary>
        [Test]
        public void LoadThroughput_ColdVersusWarm()
        {
            var fileInfo = new FileInfo(_path);

            var cold = Load();
            Assert.IsTrue(cold.Succeeded, cold.UserMessage);
            int points;
            using (var frame = cold.Frame) points = frame.Data.PointCount;

            double bestWarm = double.PositiveInfinity;
            for (int i = 0; i < 3; i++)
            {
                var warm = Load();
                Assert.IsTrue(warm.Succeeded, warm.UserMessage);
                using var frame = warm.Frame;
                bestWarm = math.min(bestWarm, warm.ElapsedMs);
            }

            foreach (var entry in _log.Snapshot())
                if (entry.Message.Contains("read ")) TestContext.WriteLine("  " + entry.Message);

            double megabytes = fileInfo.Length / (1024.0 * 1024.0);
            TestContext.WriteLine(
                $"cold {cold.ElapsedMs:F0} ms · warm {bestWarm:F0} ms · " +
                $"{megabytes / (bestWarm / 1000.0):F0} MB/s · " +
                $"{points / (bestWarm / 1000.0) / 1e6:F1} M points/s warm\n" +
                $"  one-time cost (Burst compilation etc.) ≈ {cold.ElapsedMs - bestWarm:F0} ms");

            // Warm throughput should be dominated by IO and the chunk build, not by decode.
            Assert.Less(bestWarm, 1500.0,
                $"A warm load of {megabytes:F0} MB took {bestWarm:F0} ms, which is too slow to be " +
                "explained by IO — something on the decode path is not being accelerated.");
        }

        [Test]
        public void PositionOnlyRequest_SkipsColourAndScalarWork()
        {
            var full = Load();
            Assert.IsTrue(full.Succeeded, full.UserMessage);
            long fullBytes;
            using (var frame = full.Frame) fullBytes = frame.Data.Descriptor.EstimatedBytes;

            var lean = Load(new FrameRequest(PointAttributes.Position));
            Assert.IsTrue(lean.Succeeded, lean.UserMessage);

            using var leanFrame = lean.Frame;
            Assert.AreEqual(PointAttributes.Position, leanFrame.Data.Descriptor.Attributes);
            Assert.Less(leanFrame.Data.Descriptor.EstimatedBytes, fullBytes,
                "A position-only request should allocate less than a full load.");

            TestContext.WriteLine(
                $"  full {fullBytes / (1024 * 1024)} MB vs position-only " +
                $"{leanFrame.Data.Descriptor.EstimatedBytes / (1024 * 1024)} MB");
        }
    }

    static class EnumerableExtensions
    {
        /// <summary>First element, or null — avoids pulling System.Linq in for one call.</summary>
        public static string FirstOrDefaultOrNull(this string[] values) =>
            values != null && values.Length > 0 ? values[0] : null;
    }
}
