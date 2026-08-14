using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Synthetic;
using Unity.Collections;
using Unity.Mathematics;

namespace PointCloud.Tests.EditMode
{
    /// <summary>
    /// The synthetic generator is the ground truth every other layer is checked against, so
    /// its analytic properties are asserted directly rather than assumed.
    /// </summary>
    public class SyntheticCloudTests
    {
        [SetUp]
        public void SetUp() => NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;

        [Test]
        public void SphereShell_EveryPointIsAtExactlyTheGivenRadius()
        {
            const float scale = 10f;
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, 250_000);
            settings.Scale = scale;

            using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
            var positions = data.Positions;

            float worst = 0f;
            for (int i = 0; i < positions.Length; i++)
                worst = math.max(worst, math.abs(math.length(positions[i]) - scale));

            Assert.Less(worst, 1e-3f,
                $"Worst radius deviation was {worst:E3}. A sphere shell must be exactly radius {scale} " +
                "or every test built on this shape is measuring the wrong thing.");
        }

        [Test]
        public void SphereShell_BoundsAreTheFullSphere()
        {
            const float scale = 10f;
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, 500_000);
            settings.Scale = scale;

            using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
            var bounds = data.Descriptor.LocalBounds;

            Assert.AreEqual(0f, bounds.center.magnitude, 0.05f, "Sphere shell is not centred on the origin.");
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.LessOrEqual(bounds.extents[axis], scale + 1e-3f, "Bounds exceed the sphere radius.");
                Assert.Greater(bounds.extents[axis], scale * 0.99f,
                    "Bounds do not reach the sphere radius — sampling may be biased away from the poles.");
            }
        }

        [Test]
        public void Scalar0_MatchesDistanceFromOrigin()
        {
            var settings = SyntheticCloudSettings.Default(SyntheticShape.GaussianBlob, 100_000);
            using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);

            var positions = data.Positions;
            var scalars   = data.Get(PointAttributes.Scalar0).As<uint>();

            for (int i = 0; i < positions.Length; i += 53)
                Assert.AreEqual(math.length(positions[i]), math.asfloat(scalars[i]), 1e-4f,
                    $"Scalar0 at point {i} does not equal |position|.");
        }

        [Test]
        public void RequestedAttributesAreExactlyTheAttributesProduced()
        {
            var settings = SyntheticCloudSettings.Default(SyntheticShape.Torus, 10_000);
            settings.Attributes = PointAttributes.Position | PointAttributes.Intensity | PointAttributes.Label;

            using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);

            Assert.AreEqual(settings.Attributes, data.Descriptor.Attributes);
            Assert.IsFalse(data.Descriptor.Has(PointAttributes.Color), "Colour was produced but not requested.");
            Assert.IsFalse(data.TryGet(PointAttributes.Normal, out _), "A normal stream was allocated unnecessarily.");
        }

        [Test]
        public void PositionOnlyCloud_DefaultsToTheCameraDistanceRamp()
        {
            // Requirement #1 of the tool: no colour data means colour by camera distance.
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, 1000);
            settings.Attributes = PointAttributes.Position;

            using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);

            var display = new Rendering.PointCloudDisplaySettings();
            display.ApplyDefaultsFor(data.Descriptor);

            Assert.AreEqual(Rendering.PointColorMode.ViewDepth, display.ColorMode);
            Assert.IsFalse(Rendering.PointCloudDisplaySettings.IsSupported(
                Rendering.PointColorMode.Rgb, data.Descriptor));
        }

        [Test]
        public void ColoredCloud_DefaultsToPerPointRgb()
        {
            // Requirement #2: when colour is present, use it.
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, 1000);
            using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);

            var display = new Rendering.PointCloudDisplaySettings();
            display.ApplyDefaultsFor(data.Descriptor);

            Assert.AreEqual(Rendering.PointColorMode.Rgb, display.ColorMode);
        }

        [Test]
        public void LabelsStayWithinTheRequestedCardinality()
        {
            var settings = SyntheticCloudSettings.Default(SyntheticShape.Torus, 50_000);
            settings.LabelCount = 7;

            using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
            var labels = data.Get(PointAttributes.Label).As<uint>();

            var seen = new bool[settings.LabelCount];
            for (int i = 0; i < labels.Length; i++)
            {
                Assert.Less(labels[i], (uint)settings.LabelCount, $"Label {labels[i]} exceeds the requested count.");
                seen[labels[i]] = true;
            }

            foreach (var present in seen)
                Assert.IsTrue(present, "Not every label value was produced — the legend would be misleading.");
        }

        [Test]
        public void ApplyRetention_FreesEverythingButPositions()
        {
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, 10_000);
            using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);

            Assert.IsTrue(data.TryGet(PointAttributes.Color, out _));

            data.ApplyRetention(CpuRetention.PositionsOnly);

            Assert.IsTrue(data.TryGet(PointAttributes.Position, out _), "Positions must survive; picking needs them.");
            Assert.IsFalse(data.TryGet(PointAttributes.Color, out _), "Colour stream should have been released.");
            Assert.IsFalse(data.TryGet(PointAttributes.Normal, out _), "Normal stream should have been released.");
        }

        [Test]
        public void AllShapesGenerateFiniteFiniteBoundedData()
        {
            foreach (SyntheticShape shape in System.Enum.GetValues(typeof(SyntheticShape)))
            {
                var settings = SyntheticCloudSettings.Default(shape, 20_000);
                using var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);

                var positions = data.Positions;
                for (int i = 0; i < positions.Length; i += 31)
                {
                    float3 p = positions[i];
                    Assert.IsFalse(math.any(math.isnan(p)), $"{shape} produced NaN at point {i}.");
                    Assert.IsFalse(math.any(math.isinf(p)), $"{shape} produced Inf at point {i}.");
                }

                Assert.Greater(data.Descriptor.MedianPointSpacing, 0f,
                    $"{shape} produced no spacing estimate, so points would default to a wrong size.");
            }
        }
    }
}
