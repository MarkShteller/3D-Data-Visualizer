using NUnit.Framework;
using PointCloud.Core.Data;
using PointCloud.Core.Synthetic;
using PointCloud.Rendering;
using Unity.Collections;
using UnityEngine;

namespace PointCloud.Tests.PlayMode
{
    /// <summary>
    /// End-to-end render checks. These are genuinely achievable here because everything is
    /// procedural: a known cloud, a fixed camera, one frame, then assert actual pixels.
    ///
    /// They also serve as the shader compilation test — a broken HLSL edit fails here
    /// rather than silently rendering nothing in the app.
    /// </summary>
    public class PointCloudRenderTests
    {
        const int Width  = 256;
        const int Height = 256;

        GameObject        _cameraObject;
        Camera            _camera;
        RenderTexture     _target;
        Texture2D         _readback;
        PointCloudRenderer _renderer;
        PointCloudData    _data;
        GpuPointCloud     _cloud;

        [SetUp]
        public void SetUp()
        {
            NativeLeakDetection.Mode = NativeLeakDetectionMode.EnabledWithStackTrace;

            _target = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32,
                                        RenderTextureReadWrite.sRGB) { name = "PointCloudTestTarget" };
            _target.Create();

            _cameraObject = new GameObject("TestCamera");
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.targetTexture  = _target;
            _camera.clearFlags     = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.nearClipPlane  = 0.1f;
            _camera.farClipPlane   = 500f;
            _camera.fieldOfView    = 60f;

            _readback = new Texture2D(Width, Height, TextureFormat.RGBA32, false, linear: false);
            _renderer = new PointCloudRenderer();
        }

        [TearDown]
        public void TearDown()
        {
            _renderer?.Dispose();
            _data?.Dispose();

            if (_readback != null) Object.DestroyImmediate(_readback);
            if (_cameraObject != null) Object.DestroyImmediate(_cameraObject);
            if (_target != null) { _target.Release(); Object.DestroyImmediate(_target); }
        }

        void LoadCloud(SyntheticShape shape = SyntheticShape.SphereShell, int pointCount = 200_000,
                       PointAttributes attributes = PointAttributes.Position | PointAttributes.Color)
        {
            var settings = SyntheticCloudSettings.Default(shape, pointCount);
            settings.Attributes = attributes;
            settings.Scale = 10f;

            _data  = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
            _cloud = _renderer.Add(_data);
            _cloud.UploadAll();

            // Frame the cloud head-on and close enough that it fills the middle of the view.
            var bounds = _cloud.WorldBounds;
            float distance = bounds.extents.magnitude / Mathf.Tan(0.5f * _camera.fieldOfView * Mathf.Deg2Rad);
            _cameraObject.transform.position = bounds.center + new Vector3(0f, 0f, -distance);
            _cameraObject.transform.LookAt(bounds.center);
        }

        /// <summary>
        /// Submit the draws, render the camera, and pull the pixels back.
        ///
        /// Deliberately not a coroutine yielding WaitForEndOfFrame: that never resumes under
        /// -batchmode, so a test using it hangs forever in CI instead of failing. An explicit
        /// Camera.Render() picks up the Graphics.RenderPrimitivesIndirect submissions made
        /// immediately before it, and works identically in and out of batch mode.
        /// </summary>
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

        /// <summary>Mean of a small box at the centre of the image, where the cloud is dense.</summary>
        Color SampleCenter(int radius = 6)
        {
            Color sum = Color.clear;
            int n = 0;
            for (int y = Height / 2 - radius; y <= Height / 2 + radius; y++)
            for (int x = Width / 2 - radius; x <= Width / 2 + radius; x++)
            {
                sum += _readback.GetPixel(x, y);
                n++;
            }
            return sum / n;
        }

        /// <summary>Mean of one horizontal scanline, ignoring background pixels.</summary>
        Color AverageRow(int y)
        {
            Color sum = Color.clear;
            int n = 0;
            for (int x = 0; x < Width; x++)
            {
                var p = _readback.GetPixel(x, y);
                if (p.r + p.g + p.b < 0.02f) continue;   // background
                sum += p;
                n++;
            }
            return n == 0 ? Color.clear : sum / n;
        }

        /// <summary>Lowest and highest image rows containing a meaningful number of drawn pixels.</summary>
        bool TryFindOccupiedRows(out int lowestRow, out int highestRow)
        {
            lowestRow = int.MaxValue;
            highestRow = int.MinValue;

            for (int y = 0; y < Height; y++)
            {
                int lit = 0;
                for (int x = 0; x < Width; x++)
                {
                    var p = _readback.GetPixel(x, y);
                    if (p.r + p.g + p.b > 0.02f) lit++;
                }
                // A tenth of the row, so a few stray points do not widen the band.
                if (lit < Width / 10) continue;

                lowestRow  = Mathf.Min(lowestRow, y);
                highestRow = Mathf.Max(highestRow, y);
            }

            return lowestRow <= highestRow;
        }

        int NonBackgroundPixelCount()
        {
            int count = 0;
            var pixels = _readback.GetPixels32();
            foreach (var p in pixels)
                if (p.r > 8 || p.g > 8 || p.b > 8) count++;
            return count;
        }

        [Test]
        public void ShaderCompilesAndPointsReachTheScreen()
        {
            LoadCloud();
            _cloud.Display.ColorMode = PointColorMode.Flat;
            _cloud.Display.SizeMode  = PointSizeMode.FixedPixels;
            _cloud.Display.PixelSize = 4f;

            RenderFrame();

            Assert.AreEqual(_data.PointCount, _cloud.DrawnPointCount,
                "The indirect command did not cover every uploaded point.");
            Assert.AreEqual(1, _renderer.DrawCallCount, "Expected exactly one indirect draw for one cloud.");

            int lit = NonBackgroundPixelCount();
            Assert.Greater(lit, Width * Height / 20,
                $"Only {lit} of {Width * Height} pixels were touched. The points are not reaching the screen — " +
                "check the shader compiled and that RenderParams.worldBounds encloses the cloud.");
        }

        /// <summary>
        /// The linear/sRGB regression test. A 50% grey flat colour must read back as 50% grey.
        /// If any of the three conversion points (colour uniform, framebuffer, readback) is
        /// wrong, this lands near 188 (double-encoded) or 55 (double-decoded) instead of 128.
        /// </summary>
        [Test]
        public void FlatMidGrey_ReadsBackAsMidGrey()
        {
            LoadCloud(SyntheticShape.PlaneGrid, 400_000, PointAttributes.Position);

            _cloud.Display.ColorMode = PointColorMode.Flat;
            _cloud.Display.FlatColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            _cloud.Display.SizeMode  = PointSizeMode.FixedPixels;
            _cloud.Display.PixelSize = 8f;   // guarantee full coverage at the centre

            // Look straight down at the plane so the centre of the image is solid cloud.
            var bounds = _cloud.WorldBounds;
            _cameraObject.transform.position = bounds.center + new Vector3(0f, 12f, 0f);
            _cameraObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            RenderFrame();

            var center = SampleCenter();
            Assert.AreEqual(0.5f, center.r, 0.03f,
                $"Mid-grey read back as {center.r:F3} ({center.r * 255f:F0}/255) instead of 0.5 (128/255). " +
                "An sRGB conversion is being applied twice or not at all.");
            Assert.AreEqual(center.r, center.g, 0.01f, "Grey is not neutral — channels diverged.");
            Assert.AreEqual(center.r, center.b, 0.01f, "Grey is not neutral — channels diverged.");
        }

        /// <summary>
        /// Requirement #1: with no colour data, points are coloured by camera-space distance.
        /// A plane viewed edge-on has a monotonic depth gradient, so the near and far halves
        /// of the image must sample different ends of the colormap.
        /// </summary>
        [Test]
        public void ViewDepthMode_ProducesADepthGradient()
        {
            LoadCloud(SyntheticShape.PlaneGrid, 600_000, PointAttributes.Position);

            Assert.IsFalse(_data.Descriptor.Has(PointAttributes.Color),
                "This test must run on a cloud with no colour so the fallback path is exercised.");

            _cloud.Display.ColorMode = PointColorMode.ViewDepth;
            _cloud.Display.Colormap  = Colormap.Turbo;
            _cloud.Display.SizeMode  = PointSizeMode.FixedPixels;
            _cloud.Display.PixelSize = 6f;

            // Low, shallow angle so screen-Y maps to depth across the whole plane.
            var bounds = _cloud.WorldBounds;
            _cameraObject.transform.position = bounds.center + new Vector3(0f, 3f, -14f);
            _cameraObject.transform.rotation = Quaternion.Euler(10f, 0f, 0f);

            // Several frames: the range auto-fit smooths exponentially toward the fitted range.
            for (int i = 0; i < 8; i++) RenderFrame();

            // A shallow pitch means the plane occupies only part of the frame, with empty
            // background above the horizon. Sample within the band the cloud actually covers
            // rather than at fixed image fractions, so the test measures the ramp and not
            // the framing.
            Assert.IsTrue(TryFindOccupiedRows(out int lowestRow, out int highestRow),
                "Nothing was drawn at all.");
            Assert.Greater(highestRow - lowestRow, 16,
                $"The cloud covers only rows {lowestRow}..{highestRow}; too thin to measure a gradient.");

            int span = highestRow - lowestRow;
            Color near = AverageRow(lowestRow + span / 8);        // lower in the image = closer
            Color far  = AverageRow(highestRow - span / 8);       // higher in the image = further

            Assert.Greater(near.r + near.g + near.b, 0.05f, "The near band is empty.");
            Assert.Greater(far.r + far.g + far.b, 0.05f, "The far band is empty.");

            float difference = Mathf.Abs(near.r - far.r) + Mathf.Abs(near.g - far.g) + Mathf.Abs(near.b - far.b);
            Assert.Greater(difference, 0.15f,
                $"Near and far rows are nearly the same colour (near={near}, far={far}). " +
                "The camera-space distance ramp is not varying with depth.");
        }

        [Test]
        public void RgbMode_ReproducesPerPointColor()
        {
            // Colours are a pure function of position, so a plane at a known place has a
            // predictable colour: the generator maps y through saturate(y/(2*scale) + 0.5),
            // and this plane sits at y = 0, so green is exactly 0.5 for every point.
            LoadCloud(SyntheticShape.PlaneGrid, 400_000,
                      PointAttributes.Position | PointAttributes.Color);

            _cloud.Display.ColorMode = PointColorMode.Rgb;
            _cloud.Display.SizeMode  = PointSizeMode.FixedPixels;
            _cloud.Display.PixelSize = 8f;

            var bounds = _cloud.WorldBounds;
            _cameraObject.transform.position = bounds.center + new Vector3(0f, 12f, 0f);
            _cameraObject.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            RenderFrame();

            var center = SampleCenter();

            // The synthetic generator authors colours as LINEAR values (ColorIsLinear = true),
            // so the shader must not sRGB-decode them. A linear 0.5 written to an sRGB
            // framebuffer reads back at ~0.735. Seeing 0.5 here would mean an unwanted decode
            // was applied; seeing ~0.87 would mean it was encoded twice.
            const float expected = 0.7354f;   // LinearToSrgb(0.5)
            Assert.AreEqual(expected, center.g, 0.05f,
                $"Centre green channel was {center.g:F3}, expected ~{expected:F3} for a y=0 plane " +
                "carrying linear colour. Per-point colour is wrong, or ColorIsLinear was ignored.");
        }

        [Test]
        public void HiddenCloud_IsNotDrawn()
        {
            LoadCloud();
            _cloud.Display.Visible = false;

            RenderFrame();

            Assert.AreEqual(0, _renderer.DrawCallCount);
            Assert.AreEqual(0, NonBackgroundPixelCount(), "A hidden cloud still put pixels on screen.");
        }

        [Test]
        public void ProgressiveUpload_DrawsOnlyTheResidentPrefix()
        {
            var settings = SyntheticCloudSettings.Default(SyntheticShape.SphereShell, 500_000);
            settings.Attributes = PointAttributes.Position | PointAttributes.Color;
            _data  = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
            _cloud = _renderer.Add(_data);

            Assert.AreEqual(0, _cloud.UploadedPointCount, "Nothing should be resident before the pump runs.");

            _cloud.AdvanceUpload(1024 * 1024);   // 1 MB
            int afterOneSlice = _cloud.UploadedPointCount;

            Assert.Greater(afterOneSlice, 0, "The upload pump made no progress.");
            Assert.Less(afterOneSlice, _data.PointCount, "A 1 MB slice should not cover an 8 MB cloud.");

            _cloud.SetSingleDrawCommand();
            Assert.AreEqual(afterOneSlice, _cloud.DrawnPointCount,
                "The draw command must cover exactly the resident prefix — drawing past it shows garbage.");

        }
    }
}
