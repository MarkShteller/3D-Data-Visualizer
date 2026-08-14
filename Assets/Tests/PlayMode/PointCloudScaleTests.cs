using System.Diagnostics;
using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Synthetic;
using PointCloud.Rendering;
using Unity.Collections;
using UnityEngine;

namespace PointCloud.Tests.PlayMode
{
    /// <summary>
    /// The M1 acceptance test: 20M points resident and drawn, with VRAM matching the
    /// arithmetic the data model was designed around (16 B/pt for position + colour).
    ///
    /// Correctness and memory are asserted; timings are reported rather than asserted,
    /// because a threshold that passes on this machine would be meaningless on another.
    /// Committed performance baselines belong in the perf-test suite.
    /// </summary>
    [Category("Scale")]
    public class PointCloudScaleTests
    {
        const int TwentyMillion = 20_000_000;
        const int Width = 512, Height = 512;

        GameObject         _cameraObject;
        Camera             _camera;
        RenderTexture      _target;
        PointCloudRenderer _renderer;
        PointCloudData     _data;
        GpuPointCloud      _cloud;

        [SetUp]
        public void SetUp()
        {
            _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                                        RenderTextureReadWrite.sRGB);
            _target.Create();

            _cameraObject = new GameObject("ScaleTestCamera");
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.targetTexture   = _target;
            _camera.clearFlags      = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.fieldOfView     = 60f;

            _renderer = new PointCloudRenderer();
        }

        [TearDown]
        public void TearDown()
        {
            _renderer?.Dispose();
            _data?.Dispose();
            if (_cameraObject != null) Object.DestroyImmediate(_cameraObject);
            if (_target != null) { _target.Release(); Object.DestroyImmediate(_target); }
        }

        [Test]
        public void TwentyMillionPoints_UploadAndDrawWithinTheExpectedVramBudget()
        {
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, TwentyMillion);
            settings.Attributes = PointAttributes.Position | PointAttributes.Color;
            settings.Scale = 50f;

            var stopwatch = Stopwatch.StartNew();
            _data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
            long generateMs = stopwatch.ElapsedMilliseconds;

            Assert.AreEqual(TwentyMillion, _data.PointCount);
            Assert.AreEqual(16, _data.Descriptor.BytesPerPoint,
                "position (12) + colour (4) must be 16 B/pt, or the whole VRAM budget is wrong.");

            int expectedChunks = (TwentyMillion + 131071) / 131072;
            Assert.AreEqual(expectedChunks, _data.Chunks.Length,
                $"Expected {expectedChunks} chunks at the 128K default.");

            stopwatch.Restart();
            _cloud = _renderer.Add(_data);
            _cloud.UploadAll();
            long uploadMs = stopwatch.ElapsedMilliseconds;

            Assert.IsTrue(_cloud.IsFullyUploaded);
            Assert.AreEqual(TwentyMillion, _cloud.UploadedPointCount);

            // 20M x 16 B = 320 MB, plus a rounding-error indirect args buffer.
            const long expectedBytes = (long)TwentyMillion * 16;
            Assert.That(_cloud.VramBytes, Is.EqualTo(expectedBytes).Within(expectedBytes / 100),
                $"VRAM is {_cloud.VramBytes / (1024 * 1024)} MB, expected " +
                $"~{expectedBytes / (1024 * 1024)} MB. Attribute packing regressed.");

            var bounds = _cloud.WorldBounds;
            float distance = bounds.extents.magnitude / Mathf.Tan(0.5f * _camera.fieldOfView * Mathf.Deg2Rad);
            _cameraObject.transform.position = bounds.center + new Vector3(0f, 0f, -distance);
            _cameraObject.transform.LookAt(bounds.center);

            _cloud.Display.ColorMode = PointColorMode.Rgb;
            _cloud.Display.SizeMode  = PointSizeMode.FixedPixels;
            _cloud.Display.PixelSize = 2f;

            // One warm-up frame so shader compilation and buffer residency are not timed.
            _renderer.Render(_camera);
            _camera.Render();

            const int frames = 20;
            stopwatch.Restart();
            for (int i = 0; i < frames; i++)
            {
                _renderer.Render(_camera);
                _camera.Render();
            }
            double msPerFrame = stopwatch.Elapsed.TotalMilliseconds / frames;

            Assert.AreEqual(TwentyMillion, _cloud.DrawnPointCount,
                "Not every point was submitted.");
            Assert.AreEqual(1, _renderer.DrawCallCount,
                "20M points must still be a single indirect draw.");

            TestContext.WriteLine(
                $"20M points | generate+chunk {generateMs} ms | upload {uploadMs} ms | " +
                $"{msPerFrame:F2} ms/frame at {Width}x{Height} | " +
                $"{_cloud.VramBytes / (1024 * 1024)} MB VRAM | {_data.Chunks.Length} chunks | " +
                $"{SystemInfo.graphicsDeviceName}");

            // A loose sanity bound rather than a performance assertion: 500 ms/frame would
            // mean something is fundamentally wrong (CPU readback stall, per-point draw call),
            // not merely slow hardware.
            Assert.Less(msPerFrame, 500.0,
                $"{msPerFrame:F1} ms/frame for a single indirect draw indicates a structural problem.");
        }

        [Test]
        public void TwentyMillionPoints_ProgressiveUploadFillsInOverManyFrames()
        {
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, TwentyMillion);
            settings.Attributes = PointAttributes.Position | PointAttributes.Color;

            _data  = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
            _cloud = _renderer.Add(_data);

            // 320 MB at the 8 MB/frame default budget is ~40 frames, so the cloud is visibly
            // filling in for most of a second rather than stalling.
            const long budget = 8L * 1024 * 1024;
            int frames = 0;
            int previous = 0;

            while (!_cloud.IsFullyUploaded && frames < 200)
            {
                _cloud.AdvanceUpload(budget);
                Assert.Greater(_cloud.UploadedPointCount, previous,
                    $"Frame {frames}: the upload pump stalled at {previous} points.");
                previous = _cloud.UploadedPointCount;
                frames++;
            }

            Assert.IsTrue(_cloud.IsFullyUploaded, $"Still not resident after {frames} frames.");
            Assert.That(frames, Is.InRange(35, 45),
                $"Took {frames} frames to upload 320 MB at 8 MB/frame; expected about 40.");
            TestContext.WriteLine($"20M points became resident over {frames} frames of {budget / (1024 * 1024)} MB.");
        }
    }
}
