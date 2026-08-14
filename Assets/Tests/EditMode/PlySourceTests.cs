using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Diagnostics;
using PointCloud.Core.Sources;
using PointCloud.Formats.Ply;
using PointCloud.Formats.Vrs;
using Unity.Collections;
using Unity.Mathematics;

namespace PointCloud.Tests.EditMode
{
    public class PlySourceTests
    {
        string _directory;
        SourceRegistry _registry;
        PointCloudLoader _loader;
        LoadLog _log;

        [SetUp]
        public void SetUp()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;

            _directory = PlyFixtures.CreateTempDirectory();
            _log = new LoadLog();
            _registry = new SourceRegistry();
            _registry.Register(new PlySourceFactory(_log));
            _registry.Register(new VrsSourceFactory());
            _loader = new PointCloudLoader(_registry, _log);
        }

        [TearDown]
        public void TearDown()
        {
            try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
            catch (IOException) { /* a stray handle is not a test failure */ }
        }

        /// <summary>
        /// Blocking rather than an async test method: the load runs on Task.Run with no
        /// synchronisation context, so there is nothing to dead-lock against, and a plain
        /// [Test] keeps failures readable.
        /// </summary>
        LoadResult Load(string path, FrameRequest? request = null) =>
            _loader.LoadAsync(path, request ?? FrameRequest.Default, null, CancellationToken.None)
                   .GetAwaiter().GetResult();

        static void AssertSucceeded(LoadResult result)
        {
            if (!result.Succeeded)
                Assert.Fail($"Load failed: {result.UserMessage}\n{result.Error}");
        }

        // --- happy paths ---------------------------------------------------------

        [Test]
        public void AsciiXyzRgb_LoadsEveryPointWithExactColors()
        {
            var result = Load(PlyFixtures.WriteAsciiXyzRgb(_directory));
            AssertSucceeded(result);

            using var frame = result.Frame;
            var data = frame.Data;

            Assert.AreEqual(PlyFixtures.Points.Length, data.PointCount);
            Assert.AreEqual(PointAttributes.Position | PointAttributes.Color, data.Descriptor.Attributes);

            // uchar colour is 0-255 sRGB, so the descriptor must NOT claim linear.
            Assert.IsFalse(data.Descriptor.ColorIsLinear,
                "Integer colour is sRGB; marking it linear renders every such file washed out.");

            AssertContainsExpectedPoints(data);
        }

        [Test]
        public void AsciiWithCrLf_ParsesIdentically()
        {
            var result = Load(PlyFixtures.WriteAsciiCrLf(_directory));
            AssertSucceeded(result);

            using var frame = result.Frame;
            Assert.AreEqual(PlyFixtures.Points.Length, frame.Data.PointCount);
            AssertContainsExpectedPoints(frame.Data);
        }

        [Test]
        public void BinaryLittleEndian_MatchesAsciiBitForBit()
        {
            var ascii  = Load(PlyFixtures.WriteAsciiXyzRgb(_directory));
            var binary = Load(PlyFixtures.WriteBinaryXyzRgb(_directory, bigEndian: false));
            AssertSucceeded(ascii);
            AssertSucceeded(binary);

            using var a = ascii.Frame;
            using var b = binary.Frame;

            AssertSamePositions(a.Data, b.Data, "ascii", "binary_little_endian");
        }

        /// <summary>
        /// Big-endian is the branch nobody exercises and everybody gets wrong. Comparing it
        /// against the little-endian encoding of the same values catches a byte swap that
        /// would otherwise produce plausible-looking garbage.
        /// </summary>
        [Test]
        public void BinaryBigEndian_MatchesLittleEndianBitForBit()
        {
            var little = Load(PlyFixtures.WriteBinaryXyzRgb(_directory, bigEndian: false));
            var big    = Load(PlyFixtures.WriteBinaryXyzRgb(_directory, bigEndian: true));
            AssertSucceeded(little);
            AssertSucceeded(big);

            using var l = little.Frame;
            using var b = big.Frame;

            AssertSamePositions(l.Data, b.Data, "binary_little_endian", "binary_big_endian");

            var lc = l.Data.Get(PointAttributes.Color).As<uint>();
            var bc = b.Data.Get(PointAttributes.Color).As<uint>();
            for (int i = 0; i < lc.Length; i++)
                Assert.AreEqual(lc[i], bc[i], $"Colour {i} differs between endiannesses.");
        }

        [Test]
        public void FloatColor_IsTreatedAsLinear()
        {
            var result = Load(PlyFixtures.WriteAsciiFloatColor(_directory));
            AssertSucceeded(result);

            using var frame = result.Frame;

            // 0-1 float colour is linear by Open3D/CloudCompare convention; the shader must
            // not sRGB-decode it a second time.
            Assert.IsTrue(frame.Data.Descriptor.ColorIsLinear,
                "Float colour must be flagged linear, or it renders washed out.");

            var colors = frame.Data.Get(PointAttributes.Color).As<uint>();
            AssertColorsMatchFixture(frame.Data, colors);
        }

        [Test]
        public void UnrecognisedProperties_BecomeNamedScalarFields()
        {
            var result = Load(PlyFixtures.WriteAsciiWithScalars(_directory));
            AssertSucceeded(result);

            using var frame = result.Frame;
            var descriptor = frame.Data.Descriptor;

            // scalar_intensity and scalar_label are known names behind CloudCompare's prefix.
            Assert.IsTrue(descriptor.Has(PointAttributes.Intensity),
                "'scalar_intensity' should map to Intensity once the scalar_ prefix is stripped.");
            Assert.IsTrue(descriptor.Has(PointAttributes.Label),
                "'scalar_label' should map to Label.");

            Assert.AreEqual(1, descriptor.ScalarFields.Length,
                "Only the genuinely unrecognised property should become a generic scalar.");

            var field = descriptor.ScalarFields[0];
            Assert.AreEqual("scalar_C2C_absolute_distances", field.Name,
                "The source property name must survive verbatim — it is what the engineer recognises.");
            Assert.AreEqual(ScalarSemantic.Deviation, field.Semantic,
                "A C2C distance should be guessed as a deviation so it defaults to a diverging ramp.");

            Assert.IsTrue(descriptor.Has(PointAttributes.Scalar0));
            Assert.Less(field.SourceRange.x, 0f, "The observed range should span the negative values present.");
            Assert.Greater(field.SourceRange.y, 0f);
        }

        [Test]
        public void TrailingFaceElement_IsSkipped()
        {
            var result = Load(PlyFixtures.WriteBinaryWithTrailingFaces(_directory));
            AssertSucceeded(result);

            using var frame = result.Frame;

            Assert.AreEqual(PlyFixtures.Points.Length, frame.Data.PointCount,
                "A mesh PLY should load as its vertices, ignoring the face list entirely.");
            AssertContainsExpectedPoints(frame.Data);
        }

        /// <summary>
        /// The precision case. UTM eastings near 5e5 have roughly half a metre of float32
        /// quantisation; without an origin offset such a cloud renders as a wobbling mess.
        /// </summary>
        [Test]
        public void DoublePositionsAtUtmMagnitudes_KeepSubMillimetrePrecision()
        {
            var origin = new PlyFixtures.double3(512345.678, 4321098.765, 137.5);
            var result = Load(PlyFixtures.WriteBinaryDoubleUtm(_directory, origin));
            AssertSucceeded(result);

            using var frame = result.Frame;
            var descriptor = frame.Data.Descriptor;

            Assert.AreNotEqual(0.0, math.length(descriptor.OriginOffset),
                "No origin offset was applied to a double-precision cloud.");

            // Chunk building reorders points deliberately, so every loaded point is matched
            // against its nearest expected counterpart rather than by index.
            var positions = frame.Data.Positions;
            for (int i = 0; i < positions.Length; i++)
            {
                double3 actual = descriptor.ToAbsolute(positions[i]);

                double best = double.PositiveInfinity;
                double3 nearest = default;
                foreach (var point in PlyFixtures.Points)
                {
                    var expected = new double3(origin.x + point.x, origin.y + point.y, origin.z + point.z);
                    double error = math.length(actual - expected);
                    if (error < best) { best = error; nearest = expected; }
                }

                Assert.Less(best, 1e-3,
                    $"Point {i} reconstructs to {actual}; nearest expected is {nearest} " +
                    $"(error {best:E3} m). The origin offset is not preserving precision. " +
                    "Stored raw, float32 at UTM magnitudes would be off by ~0.5 m.");
            }
        }

        [Test]
        public void OriginOffsetIsReproducible_SoTwoCopiesOverlay()
        {
            var origin = new PlyFixtures.double3(512345.678, 4321098.765, 137.5);
            var first  = Load(PlyFixtures.WriteBinaryDoubleUtm(_directory, origin, "a.ply"));
            var second = Load(PlyFixtures.WriteBinaryDoubleUtm(_directory, origin, "b.ply"));
            AssertSucceeded(first);
            AssertSucceeded(second);

            using var a = first.Frame;
            using var b = second.Frame;

            Assert.AreEqual(a.Data.Descriptor.OriginOffset.x, b.Data.Descriptor.OriginOffset.x, 1e-9);
            Assert.AreEqual(a.Data.Descriptor.OriginOffset.y, b.Data.Descriptor.OriginOffset.y, 1e-9);
            Assert.AreEqual(a.Data.Descriptor.OriginOffset.z, b.Data.Descriptor.OriginOffset.z, 1e-9);

            AssertSamePositions(a.Data, b.Data, "first load", "second load");
        }

        [Test]
        public void FrameRequest_SkipsUnwantedAttributes()
        {
            var result = Load(PlyFixtures.WriteAsciiXyzRgb(_directory),
                              new FrameRequest(PointAttributes.Position));
            AssertSucceeded(result);

            using var frame = result.Frame;

            Assert.IsFalse(frame.Data.TryGet(PointAttributes.Color, out _),
                "Colour was decoded despite not being requested.");
            Assert.AreEqual(PointAttributes.Position, frame.Data.Descriptor.Attributes);
        }

        [Test]
        public void LoadedCloudIsChunkedAndReadyToDraw()
        {
            var result = Load(PlyFixtures.WriteAsciiXyzRgb(_directory));
            AssertSucceeded(result);

            using var frame = result.Frame;

            Assert.Greater(frame.Data.Chunks.Length, 0, "No chunk table was built.");
            Assert.AreEqual(frame.Data.PointCount, frame.Data.Chunks[0].Count);
            Assert.Greater(frame.Data.Descriptor.MedianPointSpacing, 0f,
                "Without a spacing estimate the cloud opens looking like static.");
        }

        // --- robustness ----------------------------------------------------------
        // Every one of these must fail with a typed, informative error — never hang,
        // never OOM, never take the editor down.

        [Test]
        public void TruncatedBinaryFile_FailsWithTheByteOffset()
        {
            var result = Load(PlyFixtures.WriteTruncated(_directory));

            Assert.IsFalse(result.Succeeded);
            Assert.IsInstanceOf<PointCloudFormatException>(result.Error);
            StringAssert.Contains("truncated", result.UserMessage.ToLowerInvariant());
        }

        [Test]
        public void GarbageFile_IsRejectedByTheRegistry()
        {
            var path = PlyFixtures.WriteGarbage(_directory);

            // Content sniffing should refuse it despite the .ply extension.
            Assert.IsFalse(_registry.TryResolve(path, out _),
                "A file with no 'ply' magic should not resolve to the PLY reader.");

            var result = Load(path);
            Assert.IsFalse(result.Succeeded);
            Assert.IsInstanceOf<PointCloudUnsupportedException>(result.Error);
        }

        [Test]
        public void HeaderWithoutEndHeader_Fails()
        {
            var result = Load(PlyFixtures.WriteNoEndHeader(_directory));

            Assert.IsFalse(result.Succeeded);
            Assert.IsInstanceOf<PointCloudFormatException>(result.Error);
            StringAssert.Contains("end_header", result.UserMessage);
        }

        [Test]
        public void UnknownPropertyType_Fails()
        {
            var result = Load(PlyFixtures.WriteUnknownType(_directory));

            Assert.IsFalse(result.Succeeded);
            Assert.IsInstanceOf<PointCloudFormatException>(result.Error);
            StringAssert.Contains("quadruple", result.UserMessage);
        }

        [Test]
        public void FileWithNoPositions_ExplainsWhyItCannotBeShown()
        {
            var result = Load(PlyFixtures.WriteNoPositions(_directory));

            Assert.IsFalse(result.Succeeded);
            Assert.IsInstanceOf<PointCloudFormatException>(result.Error);
            StringAssert.Contains("x/y/z", result.UserMessage);
        }

        [Test]
        public void AsciiBodyShorterThanDeclared_Fails()
        {
            var result = Load(PlyFixtures.WriteAsciiShortBody(_directory));

            Assert.IsFalse(result.Succeeded);
            Assert.IsInstanceOf<PointCloudFormatException>(result.Error);
            StringAssert.Contains("100", result.UserMessage);
        }

        [Test]
        public void MissingFile_FailsCleanly()
        {
            var result = Load(Path.Combine(_directory, "does_not_exist.ply"));

            Assert.IsFalse(result.Succeeded);
            Assert.IsInstanceOf<FileNotFoundException>(result.Error);
        }

        [Test]
        public void CancellationMidLoad_LeaksNothing()
        {
            var path = PlyFixtures.WriteBinaryXyzRgb(_directory, bigEndian: false);

            // Repeated so a leak accumulates into something the detector will catch. Domain
            // reload is disabled here, so a leaked allocation outlives the play session.
            for (int i = 0; i < 50; i++)
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();

                var result = _loader.LoadAsync(path, FrameRequest.Default, null, cts.Token)
                                    .GetAwaiter().GetResult();

                Assert.IsTrue(result.Cancelled || !result.Succeeded);
                result.Frame?.Dispose();
            }
        }

        // --- registry and the VRS stub -------------------------------------------

        [Test]
        public void Registry_ResolvesPlyByContentNotExtension()
        {
            var path = PlyFixtures.WriteAsciiXyzRgb(_directory, "misnamed.pointcloud");

            Assert.IsTrue(_registry.TryResolve(path, out var factory),
                "A PLY with an unusual extension should still be recognised by its magic bytes.");
            Assert.AreEqual("ply", factory.Id);
        }

        [Test]
        public void VrsFile_FailsWithAnActionableNotYetSupportedMessage()
        {
            var path = Path.Combine(_directory, "recording.vrs");
            File.WriteAllBytes(path, new byte[] { 0x56, 0x69, 0x73, 0x69, 0, 0, 0, 0 });

            Assert.IsTrue(_registry.TryResolve(path, out var factory),
                "VRS must resolve to its stub so the whole discovery path is exercised.");
            Assert.AreEqual("vrs", factory.Id);

            var result = Load(path);

            Assert.IsFalse(result.Succeeded);
            Assert.IsInstanceOf<PointCloudUnsupportedException>(result.Error);
            // "Not built yet" and "your file is broken" send the user down different paths.
            StringAssert.Contains("not", result.UserMessage.ToLowerInvariant());
            StringAssert.Contains("PLY", result.UserMessage);
        }

        [Test]
        public void Registry_ReportsItsSupportedExtensions()
        {
            CollectionAssert.AreEquivalent(new[] { ".ply", ".vrs" }, _registry.SupportedExtensions);
        }

        // --- helpers -------------------------------------------------------------

        static void AssertContainsExpectedPoints(PointCloudData data)
        {
            // ChunkBuilder reorders points, so compare as sets rather than by index.
            var positions = data.Positions;

            foreach (var expected in PlyFixtures.Points)
            {
                var target = new float3((float)expected.x, (float)expected.y, (float)expected.z);
                bool found = false;

                for (int i = 0; i < positions.Length && !found; i++)
                    found = math.all(math.abs(positions[i] - target) < 1e-4f);

                Assert.IsTrue(found, $"Point {target} is missing from the loaded cloud.");
            }
        }

        static void AssertColorsMatchFixture(PointCloudData data, NativeArray<uint> colors)
        {
            var positions = data.Positions;

            for (int i = 0; i < positions.Length; i++)
            {
                // Find which fixture point this is, since order is not preserved.
                foreach (var expected in PlyFixtures.Points)
                {
                    var target = new float3((float)expected.x, (float)expected.y, (float)expected.z);
                    if (!math.all(math.abs(positions[i] - target) < 1e-4f)) continue;

                    uint packed = colors[i];
                    Assert.AreEqual(expected.r, packed & 0xFF, $"Red mismatch at {target}");
                    Assert.AreEqual(expected.g, (packed >> 8) & 0xFF, $"Green mismatch at {target}");
                    Assert.AreEqual(expected.b, (packed >> 16) & 0xFF, $"Blue mismatch at {target}");
                    break;
                }
            }
        }

        static void AssertSamePositions(PointCloudData a, PointCloudData b, string nameA, string nameB)
        {
            Assert.AreEqual(a.PointCount, b.PointCount, $"{nameA} and {nameB} have different point counts.");

            var pa = a.Positions;
            var pb = b.Positions;

            // Both go through the same deterministic chunk build, so order matches too.
            for (int i = 0; i < pa.Length; i++)
                if (!math.all(pa[i] == pb[i]))
                    Assert.Fail($"Point {i} differs: {nameA} has {pa[i]}, {nameB} has {pb[i]}.");
        }
    }
}
