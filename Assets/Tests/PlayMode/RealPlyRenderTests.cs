using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Diagnostics;
using PointCloud.Core.Sources;
using PointCloud.Formats.Ply;
using PointCloud.Rendering;
using Unity.Collections;
using UnityEngine;

namespace PointCloud.Tests.PlayMode
{
    /// <summary>
    /// Loads a real exporter's file and puts it on screen.
    ///
    /// The EditMode tests prove the bytes are parsed correctly; this proves the whole chain
    /// works — parse, chunk, upload, draw — against data this project did not author.
    /// </summary>
    public class RealPlyRenderTests
    {
        const int Width = 384, Height = 384;

        GameObject         _cameraObject;
        Camera             _camera;
        RenderTexture      _target;
        Texture2D          _readback;
        PointCloudRenderer _renderer;
        PointCloudFrame    _frame;
        string             _path;

        [SetUp]
        public void SetUp()
        {
            var directory = Path.Combine(Application.dataPath, "Resources");
            _path = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.ply", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;

            if (_path == null)
                Assert.Ignore("No .ply in Assets/Resources — this validation needs a real sample file.");

            PointCloud.Core.JobWarmup.Run();
            PointCloud.Formats.FormatJobWarmup.Run();

            _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                                        RenderTextureReadWrite.sRGB);
            _target.Create();

            _cameraObject = new GameObject("RealPlyCamera");
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.targetTexture   = _target;
            _camera.clearFlags      = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.fieldOfView     = 55f;

            _readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false, linear: false);
            _renderer = new PointCloudRenderer();
        }

        [TearDown]
        public void TearDown()
        {
            _renderer?.Dispose();
            _frame?.Dispose();
            if (_readback != null) Object.DestroyImmediate(_readback);
            if (_cameraObject != null) Object.DestroyImmediate(_cameraObject);
            if (_target != null) { _target.Release(); Object.DestroyImmediate(_target); }
        }

        GpuPointCloud LoadAndUpload()
        {
            var log = new LoadLog();
            var registry = new SourceRegistry();
            registry.Register(new PlySourceFactory(log));

            var result = new PointCloudLoader(registry, log)
                .LoadAsync(_path, FrameRequest.Default, null, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Succeeded, result.UserMessage);
            _frame = result.Frame;

            var cloud = _renderer.Add(_frame.Data);
            cloud.UploadAll();

            var bounds = cloud.WorldBounds;
            float distance = bounds.extents.magnitude / Mathf.Tan(0.5f * _camera.fieldOfView * Mathf.Deg2Rad);
            _cameraObject.transform.position = bounds.center + new Vector3(0f, bounds.extents.y * 0.4f, -distance);
            _cameraObject.transform.LookAt(bounds.center);
            _camera.nearClipPlane = Mathf.Max(distance * 0.001f, 1e-3f);
            _camera.farClipPlane  = distance + bounds.extents.magnitude * 4f;

            return cloud;
        }

        void RenderFrame()
        {
            _renderer.Render(_camera);
            _camera.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = _target;
            _readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
            _readback.Apply();
            RenderTexture.active = previous;
        }

        int LitPixels()
        {
            int count = 0;
            foreach (var p in _readback.GetPixels32())
                if (p.r > 8 || p.g > 8 || p.b > 8) count++;
            return count;
        }

        [Test]
        public void RealFile_UploadsAndRendersItsOwnColours()
        {
            var cloud = LoadAndUpload();
            var descriptor = cloud.Descriptor;

            Assert.IsTrue(descriptor.Has(PointAttributes.Color),
                "This sample carries per-point colour, so RGB mode should be the default.");
            Assert.AreEqual(PointColorMode.Rgb, cloud.Display.ColorMode,
                "A cloud with colour must default to per-point RGB — requirement #2.");

            cloud.Display.SizeMode  = PointSizeMode.FixedPixels;
            cloud.Display.PixelSize = 3f;

            RenderFrame();

            Assert.AreEqual(descriptor.PointCount, cloud.DrawnPointCount);
            Assert.AreEqual(1, _renderer.DrawCallCount,
                "Two million points should still be a single indirect draw.");

            int lit = LitPixels();
            Assert.Greater(lit, Width * Height / 25,
                $"Only {lit} of {Width * Height} pixels were touched — the cloud is not reaching the screen.");

            // Real scan colour is varied; a uniform result would mean the colour buffer is
            // not actually being read.
            var pixels = _readback.GetPixels32();
            var distinct = new System.Collections.Generic.HashSet<int>();
            foreach (var p in pixels)
                if (p.r > 8 || p.g > 8 || p.b > 8)
                    distinct.Add((p.r >> 3) << 10 | (p.g >> 3) << 5 | (p.b >> 3));

            Assert.Greater(distinct.Count, 32,
                $"Only {distinct.Count} distinct colours rendered; per-point colour is not being applied.");

            TestContext.WriteLine(
                $"{Path.GetFileName(_path)}: {descriptor.PointCount:N0} points, " +
                $"{cloud.VramBytes / (1024 * 1024)} MB VRAM, {lit:N0} lit pixels, " +
                $"{distinct.Count} distinct colours, {_renderer.DrawCallCount} draw call");
        }

        /// <summary>
        /// Requirement #1: with colour suppressed, the cloud must still be legible via the
        /// camera-space distance ramp. This is the fallback a position-only file gets.
        /// </summary>
        [Test]
        public void RealFile_DepthRampWorksWhenColourIsIgnored()
        {
            var cloud = LoadAndUpload();

            cloud.Display.ColorMode = PointColorMode.ViewDepth;
            cloud.Display.Colormap  = Colormap.Turbo;
            cloud.Display.SizeMode  = PointSizeMode.FixedPixels;
            cloud.Display.PixelSize = 3f;

            for (int i = 0; i < 6; i++) RenderFrame();

            int lit = LitPixels();
            Assert.Greater(lit, Width * Height / 25, "Nothing rendered in depth-ramp mode.");

            var range = cloud.Display.ScalarRange;
            Assert.Greater(range.y, range.x,
                "The auto-fitted depth range is empty, so every point maps to one colormap entry.");
            Assert.Greater(range.x, 0f, "The near end of the depth range should be in front of the camera.");

            TestContext.WriteLine(
                $"depth range auto-fitted to [{range.x:F2}, {range.y:F2}] m, {lit:N0} lit pixels");
        }
    }
}
