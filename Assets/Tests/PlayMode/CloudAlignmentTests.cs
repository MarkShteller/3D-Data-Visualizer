using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Synthetic;
using PointCloud.Rendering;
using Unity.Collections;
using UnityEngine;

namespace PointCloud.Tests.PlayMode
{
    /// <summary>
    /// Zeroing a cloud's world position.
    ///
    /// The problem it solves: two clouds captured in different coordinate frames — a scan
    /// and a prediction, or the same scene from two sessions — can land kilometres apart,
    /// making visual comparison impossible. Centring each on the origin brings them
    /// together. It must be a display transform only, and it must be exactly reversible.
    /// </summary>
    public class CloudAlignmentTests
    {
        PointCloudRenderer _renderer;
        PointCloudData _a, _b;

        [SetUp]
        public void SetUp()
        {
            PointCloud.Core.JobWarmup.Run();
            _renderer = new PointCloudRenderer();
        }

        [TearDown]
        public void TearDown()
        {
            _renderer?.Dispose();
            _a?.Dispose();
            _b?.Dispose();
        }

        static PointCloudData Cloud(SyntheticShape shape, int points = 20_000)
        {
            var settings = SyntheticCloudSettings.Default(shape, points);
            settings.Attributes = PointAttributes.Position | PointAttributes.Color;
            return SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
        }

        /// <summary>Place a cloud far from the origin, as a differently-referenced capture would be.</summary>
        static void OffsetTo(GpuPointCloud cloud, Vector3 position)
        {
            cloud.BaseTransform = Matrix4x4.Translate(position);
            cloud.ApplyTransform();
        }

        [Test]
        public void ZeroingCentresACloudOnTheOrigin()
        {
            _a = Cloud(SyntheticShape.Torus);
            var cloud = _renderer.Add(_a);
            OffsetTo(cloud, new Vector3(5000f, -200f, 12000f));

            Assert.Greater(cloud.WorldBounds.center.magnitude, 1000f,
                "Precondition: the cloud should start far from the origin.");

            cloud.CenterAtOrigin();

            Assert.Less(cloud.WorldBounds.center.magnitude, 1e-2f,
                $"After zeroing, the cloud centre is at {cloud.WorldBounds.center}, not the origin.");
            Assert.IsTrue(cloud.IsTranslated, "The cloud should report that it has been moved.");
        }

        [Test]
        public void ZeroingIsIdempotent()
        {
            _a = Cloud(SyntheticShape.Torus);
            var cloud = _renderer.Add(_a);
            OffsetTo(cloud, new Vector3(5000f, -200f, 12000f));

            cloud.CenterAtOrigin();
            var first = cloud.Translation;
            cloud.CenterAtOrigin();

            // Computed from the untranslated base transform, so pressing the button twice
            // must not drift the cloud further each time.
            Assert.AreEqual(first.x, cloud.Translation.x, 1e-3f);
            Assert.AreEqual(first.y, cloud.Translation.y, 1e-3f);
            Assert.AreEqual(first.z, cloud.Translation.z, 1e-3f);
        }

        [Test]
        public void ResetRestoresTheSourcePositionExactly()
        {
            _a = Cloud(SyntheticShape.Torus);
            var cloud = _renderer.Add(_a);
            OffsetTo(cloud, new Vector3(5000f, -200f, 12000f));

            var original = cloud.WorldBounds.center;

            cloud.CenterAtOrigin();
            cloud.ResetTransform();

            Assert.IsFalse(cloud.IsTranslated);
            Assert.AreEqual(original.x, cloud.WorldBounds.center.x, 1e-2f);
            Assert.AreEqual(original.y, cloud.WorldBounds.center.y, 1e-2f);
            Assert.AreEqual(original.z, cloud.WorldBounds.center.z, 1e-2f);
        }

        /// <summary>The actual use case: two far-apart clouds become comparable.</summary>
        [Test]
        public void ZeroingTwoClouds_BringsThemIntoTheSamePlace()
        {
            _a = Cloud(SyntheticShape.Torus);
            _b = Cloud(SyntheticShape.SphereShell);

            var first  = _renderer.Add(_a);
            var second = _renderer.Add(_b);

            OffsetTo(first,  new Vector3(512345f, 0f, 4321098f));   // UTM-like
            OffsetTo(second, new Vector3(-8000f, 300f, 250f));      // a different frame entirely

            float apart = Vector3.Distance(first.WorldBounds.center, second.WorldBounds.center);
            Assert.Greater(apart, 100000f, "Precondition: the clouds should start far apart.");

            first.CenterAtOrigin();
            second.CenterAtOrigin();

            float together = Vector3.Distance(first.WorldBounds.center, second.WorldBounds.center);
            Assert.Less(together, 1e-2f,
                $"After zeroing both, their centres are {together:F3} units apart — they should overlay.");

            // And they must actually overlap in space, not merely share a centre point.
            Assert.IsTrue(first.WorldBounds.Intersects(second.WorldBounds),
                "Zeroed clouds should occupy overlapping volumes.");

            TestContext.WriteLine($"{apart:N0} units apart → {together:E2} after zeroing");
        }

        [Test]
        public void ZeroingDoesNotModifyThePointData()
        {
            _a = Cloud(SyntheticShape.Torus);
            var before = _a.Positions[0];

            var cloud = _renderer.Add(_a);
            OffsetTo(cloud, new Vector3(5000f, -200f, 12000f));
            cloud.CenterAtOrigin();

            Assert.AreEqual(before.x, _a.Positions[0].x,
                "Zeroing must be a display transform; the source data must be untouched.");
            Assert.AreEqual(before.y, _a.Positions[0].y);
            Assert.AreEqual(before.z, _a.Positions[0].z);
        }

        /// <summary>
        /// Re-orienting a cloud must not silently undo an alignment — the two transforms are
        /// kept separate precisely so they compose.
        /// </summary>
        [Test]
        public void ChangingUpAxisPreservesTheAlignment()
        {
            _a = Cloud(SyntheticShape.Torus);
            var cloud = _renderer.Add(_a);
            OffsetTo(cloud, new Vector3(5000f, -200f, 12000f));
            cloud.CenterAtOrigin();

            var translation = cloud.Translation;

            cloud.BaseTransform = CoordinateConvention.SourceToWorld(SourceUpAxis.ZUp);
            cloud.ApplyTransform();

            Assert.AreEqual(translation.x, cloud.Translation.x, 1e-4f,
                "Re-orienting the cloud discarded its alignment offset.");
            Assert.IsTrue(cloud.IsTranslated);
        }

        [Test]
        public void ChunkRaycastFindsTheCloudAndMissesEmptySpace()
        {
            _a = Cloud(SyntheticShape.SphereShell, 100_000);
            var cloud = _renderer.Add(_a);

            var center = cloud.WorldBounds.center;
            float radius = cloud.WorldBounds.extents.magnitude;

            var hitRay = new Ray(center + Vector3.back * (radius * 3f), Vector3.forward);
            Assert.IsTrue(cloud.TryRaycastChunks(hitRay, out float distance),
                "A ray aimed at the cloud centre should hit a chunk.");
            Assert.Greater(distance, 0f);
            Assert.Less(distance, radius * 4f);

            // A chunk hit must be tighter than the whole-cloud box, or zoom-to-cursor stops
            // short of the geometry the user is aiming at.
            Assert.IsTrue(cloud.WorldBounds.IntersectRay(hitRay, out float boundsDistance));
            Assert.GreaterOrEqual(distance, boundsDistance - 1e-3f,
                "A chunk hit should never be nearer than the enclosing bounds hit.");

            var missRay = new Ray(center + Vector3.back * (radius * 3f), Vector3.up);
            Assert.IsFalse(cloud.TryRaycastChunks(missRay, out _),
                "A ray pointing away from the cloud should miss.");

            TestContext.WriteLine($"chunk hit at {distance:F3}, bounds hit at {boundsDistance:F3} " +
                                  $"({distance - boundsDistance:F3} tighter)");
        }
    }
}
