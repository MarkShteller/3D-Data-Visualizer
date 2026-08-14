using NUnit.Framework;
using PointCloud.App.CameraControl;
using UnityEngine;

namespace PointCloud.Tests.EditMode
{
    /// <summary>
    /// The zoom law.
    ///
    /// The property that matters is not "zoom feels nice" but that a notch moves a fixed
    /// fraction of the distance to the SURFACE rather than to the orbit pivot. That is what
    /// makes zooming fast at range, gentle up close, and incapable of shooting through the
    /// cloud. These call the same function the controller uses.
    /// </summary>
    public class ZoomTests
    {
        const float Sensitivity = 0.35f;

        [Test]
        public void OneNotchCoversTheSameFractionAtEveryScale()
        {
            float farRatio  = OrbitFlyController.ComputeZoomRatio(1000f, 1f, Sensitivity);
            float nearRatio = OrbitFlyController.ComputeZoomRatio(0.5f, 1f, Sensitivity);

            Assert.AreEqual(farRatio, nearRatio, 1e-5f,
                $"A notch covers {1f - farRatio:P1} of the gap at 1000 m but {1f - nearRatio:P1} at 0.5 m. " +
                "The fraction must not depend on absolute scale, or zooming feels different at every distance.");
        }

        [Test]
        public void AbsoluteStepScalesWithDistanceToTheSurface()
        {
            float farStep  = 1000f * (1f - OrbitFlyController.ComputeZoomRatio(1000f, 1f, Sensitivity));
            float nearStep = 0.5f  * (1f - OrbitFlyController.ComputeZoomRatio(0.5f, 1f, Sensitivity));

            Assert.Greater(farStep, nearStep * 100f,
                $"A notch moves {farStep:F2} m at 1000 m but {nearStep:F4} m at 0.5 m — " +
                "the whole point of scaling by surface distance.");
        }

        [Test]
        public void ScrollingInwardApproachesTheSurfaceButNeverCrossesIt()
        {
            float distance = 100f;
            int notchesToConverge = -1;

            for (int notch = 0; notch < 200; notch++)
            {
                float next = distance * OrbitFlyController.ComputeZoomRatio(distance, 1f, Sensitivity);

                Assert.Greater(next, 0f, $"Distance went non-positive after {notch} notches.");
                Assert.LessOrEqual(next, distance, $"Notch {notch} moved the camera backwards.");

                // Approach is asymptotic until the absolute floor, where it parks rather than
                // continuing through the surface into negative distance.
                if (next >= distance && notchesToConverge < 0) notchesToConverge = notch;
                distance = next;
            }

            Assert.Greater(distance, 0f,
                "Zoom must approach the surface asymptotically, never pass through it.");
            Assert.Greater(notchesToConverge, 20,
                $"Zoom reached its floor after only {notchesToConverge} notches from 100 m — " +
                "each step should be a fraction of the remaining gap, not a leap toward it.");
        }

        [Test]
        public void ScrollingOutwardIncreasesDistance()
        {
            float ratio = OrbitFlyController.ComputeZoomRatio(10f, -1f, Sensitivity);

            Assert.Greater(ratio, 1f, "Scrolling out must increase the view distance.");
            Assert.AreEqual(1f / OrbitFlyController.ComputeZoomRatio(10f, 1f, Sensitivity), ratio, 1e-4f,
                "In and out should be exact inverses, or zooming does not return you where you started.");
        }

        [Test]
        public void SensitivityScalesTheStepMonotonically()
        {
            float previous = 0f;

            // Across the full range the slider exposes, 0.05 to 5.
            foreach (float sensitivity in new[] { 0.05f, 0.35f, 1f, 2.5f, 5f })
            {
                float covered = 1f - OrbitFlyController.ComputeZoomRatio(100f, 1f, sensitivity);
                Assert.Greater(covered, previous,
                    $"Zoom rate {sensitivity} did not cover more ground per notch than the rate below it.");
                previous = covered;
            }
        }

        /// <summary>
        /// At the top of the slider range one notch covers over 99% of the gap. That is a
        /// legitimate choice for crossing a large scene quickly, but it must still not put
        /// the camera through the surface or produce a degenerate distance.
        /// </summary>
        [Test]
        public void MaximumZoomRateStillCannotCrossTheSurface()
        {
            const float maxRate = 5f;
            float distance = 10_000f;

            for (int notch = 0; notch < 50; notch++)
            {
                float next = distance * OrbitFlyController.ComputeZoomRatio(distance, 1f, maxRate);

                Assert.Greater(next, 0f, $"Distance went non-positive after {notch} notches at rate {maxRate}.");
                Assert.LessOrEqual(next, distance, $"Notch {notch} moved the camera backwards.");
                distance = next;
            }

            Assert.Greater(distance, 0f,
                "Even at the maximum zoom rate the approach must stay asymptotic.");

            // One notch at the top rate should be dramatic but not total.
            float covered = 1f - OrbitFlyController.ComputeZoomRatio(100f, 1f, maxRate);
            Assert.Greater(covered, 0.99f, "The maximum rate should be genuinely fast.");
            Assert.Less(covered, 1f, "A notch must never cover the entire gap.");
        }

        [Test]
        public void DegenerateReferenceDistanceIsHandled()
        {
            // A camera sitting exactly on the surface must not produce NaN or a zero ratio.
            foreach (float reference in new[] { 0f, -5f, float.Epsilon })
            {
                float ratio = OrbitFlyController.ComputeZoomRatio(reference, 1f, Sensitivity);
                Assert.IsFalse(float.IsNaN(ratio) || float.IsInfinity(ratio),
                    $"Reference distance {reference} produced ratio {ratio}.");
                Assert.Greater(ratio, 0f);
            }
        }

        [Test]
        public void FramingPlacesTheCameraOutsideTheBounds()
        {
            var cameraObject = new GameObject("ZoomTestCamera");
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.fieldOfView = 60f;

                var controller = new OrbitFlyController();
                var bounds = new Bounds(new Vector3(5f, 0f, -3f), new Vector3(10f, 4f, 10f));
                controller.Frame(camera, bounds, animate: false);

                Assert.AreEqual(bounds.center.x, controller.Pivot.x, 1e-3f);
                Assert.AreEqual(bounds.center.z, controller.Pivot.z, 1e-3f);
                Assert.Greater(controller.Distance, bounds.extents.magnitude,
                    "Framing must place the camera outside the bounding sphere.");
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
