using System;
using System.Collections.Generic;
using PointCloud.Core.Data;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace PointCloud.Rendering
{
    /// <summary>
    /// Owns every resident cloud and submits their draws.
    ///
    /// Draws are issued from LateUpdate-style code via Graphics.RenderPrimitivesIndirect
    /// rather than from RenderPipelineManager.beginCameraRendering — in URP 17 that callback
    /// fires *after* URP has already culled, so anything submitted there is a frame late.
    /// </summary>
    public sealed class PointCloudRenderer : IDisposable
    {
        static class Props
        {
            public static readonly int ColormapLUT     = Shader.PropertyToID("_ColormapLUT");
            public static readonly int ColormapRowCount = Shader.PropertyToID("_ColormapRowCount");
            public static readonly int ColormapIndex   = Shader.PropertyToID("_ColormapIndex");
            public static readonly int ColorMode       = Shader.PropertyToID("_ColorMode");
            public static readonly int ScalarRange     = Shader.PropertyToID("_ScalarRange");
            public static readonly int LogRamp         = Shader.PropertyToID("_LogRamp");
            public static readonly int RampAxis        = Shader.PropertyToID("_RampAxis");
            public static readonly int FlatColor       = Shader.PropertyToID("_FlatColor");
            public static readonly int CloudColor      = Shader.PropertyToID("_CloudColor");
            public static readonly int Opacity         = Shader.PropertyToID("_Opacity");
            public static readonly int SizeMode        = Shader.PropertyToID("_SizeMode");
            public static readonly int PointPixelSize  = Shader.PropertyToID("_PointPixelSize");
            public static readonly int PointWorldRadius = Shader.PropertyToID("_PointWorldRadius");
            public static readonly int MinPixelSize    = Shader.PropertyToID("_MinPixelSize");
            public static readonly int MaxPixelSize    = Shader.PropertyToID("_MaxPixelSize");
            public static readonly int ColorIsSRGB     = Shader.PropertyToID("_ColorIsSRGB");
            public static readonly int CloudToWorld    = Shader.PropertyToID("_CloudToWorld");
            public static readonly int PointCount      = Shader.PropertyToID("_PointCount");
        }

        const string HasColorKeyword   = "_HAS_COLOR";
        const string HasNormalKeyword  = "_HAS_NORMAL";
        const string ShapeCircleKeyword = "_SHAPE_CIRCLE";

        readonly List<GpuPointCloud> _clouds = new();
        readonly ColormapLibrary _colormaps;
        readonly Shader _shader;
        bool _disposed;

        /// <summary>Bytes pushed to the GPU per frame across all clouds. ~8 MB keeps loads hitch-free.</summary>
        public long UploadBudgetPerFrame = 8L * 1024 * 1024;

        /// <summary>Layer the point draws are submitted on.</summary>
        public int Layer;

        public IReadOnlyList<GpuPointCloud> Clouds => _clouds;

        public ColormapLibrary Colormaps => _colormaps;

        // Frame statistics for the HUD.
        public int  TotalPointCount { get; private set; }
        public int  DrawnPointCount { get; private set; }
        public int  DrawCallCount   { get; private set; }
        public long VramBytes       { get; private set; }

        public PointCloudRenderer(Shader shader = null)
        {
            _shader = shader != null ? shader : Shader.Find("PointCloud/Points");
            if (_shader == null)
                throw new InvalidOperationException(
                    "Shader 'PointCloud/Points' not found. It must be in the build " +
                    "(Assets/Rendering/Shaders/PointCloud.shader) or referenced by a scene object.");

            _colormaps = new ColormapLibrary();
        }

        public GpuPointCloud Add(PointCloudData data)
        {
            var cloud = new GpuPointCloud(data, _shader);
            cloud.Material.SetTexture(Props.ColormapLUT, _colormaps.Lut);
            cloud.Material.SetFloat(Props.ColormapRowCount, _colormaps.Count);

            // Assign a distinct overlay colour so CloudIndex mode separates clouds on sight.
            cloud.Display.CloudColor = OverlayColor(_clouds.Count);

            SelectScalarSource(cloud);
            _clouds.Add(cloud);
            return cloud;
        }

        public void Remove(GpuPointCloud cloud)
        {
            if (cloud == null) return;
            _clouds.Remove(cloud);
            cloud.Dispose();
        }

        public void Clear()
        {
            foreach (var cloud in _clouds) cloud.Dispose();
            _clouds.Clear();
        }

        /// <summary>Advance uploads, refresh uniforms, and submit one indirect draw per cloud.</summary>
        public void Render(Camera camera)
        {
            if (_disposed || camera == null) return;

            TotalPointCount = 0;
            DrawnPointCount = 0;
            DrawCallCount   = 0;
            VramBytes       = 0;

            long uploadBudget = UploadBudgetPerFrame;

            foreach (var cloud in _clouds)
            {
                TotalPointCount += cloud.Descriptor.PointCount;
                VramBytes       += cloud.VramBytes;

                if (!cloud.IsFullyUploaded && uploadBudget > 0)
                {
                    long before = cloud.UploadedPointCount;
                    cloud.AdvanceUpload(uploadBudget);
                    // Charge roughly what was consumed so one huge cloud cannot starve the rest.
                    uploadBudget -= Math.Max(0, cloud.UploadedPointCount - before) * cloud.Descriptor.BytesPerPoint;
                }

                if (!cloud.Display.Visible || cloud.UploadedPointCount <= 0) continue;

                AutoFitRange(cloud, camera);
                PushUniforms(cloud);

                cloud.SetSingleDrawCommand();
                if (cloud.CommandCount == 0) continue;

                var renderParams = new RenderParams(cloud.Material)
                {
                    // A hard cull, not a hint: if this does not enclose the draw, Unity skips it.
                    worldBounds       = cloud.WorldBounds,
                    camera            = camera,
                    layer             = Layer,
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows    = false,
                    motionVectorMode  = MotionVectorGenerationMode.ForceNoMotion,
                    lightProbeUsage   = LightProbeUsage.Off,
                    reflectionProbeUsage = ReflectionProbeUsage.Off,
                };

                Graphics.RenderPrimitivesIndirect(in renderParams, MeshTopology.Triangles,
                                                  cloud.IndirectArgs, cloud.CommandCount);

                DrawnPointCount += cloud.DrawnPointCount;
                DrawCallCount   += cloud.CommandCount;
            }
        }

        void PushUniforms(GpuPointCloud cloud)
        {
            var material = cloud.Material;
            var display  = cloud.Display;
            var descriptor = cloud.Descriptor;

            material.SetInteger(Props.ColorMode, (int)display.ColorMode);
            material.SetInteger(Props.ColormapIndex, (int)display.Colormap);
            material.SetVector(Props.ScalarRange, new Vector4(display.ScalarRange.x, display.ScalarRange.y, 0f, 0f));
            material.SetFloat(Props.LogRamp, display.LogRamp);
            material.SetInteger(Props.RampAxis, display.RampAxis);

            // SetVector with an explicit .linear rather than SetColor: Material.SetColor only
            // applies the gamma-to-linear conversion for properties declared as Color in the
            // Properties block, and these are CBUFFER-only. Being explicit here means the
            // shader always receives linear values regardless of how the property is declared.
            material.SetVector(Props.FlatColor, display.FlatColor.linear);
            material.SetVector(Props.CloudColor, display.CloudColor.linear);
            material.SetFloat(Props.Opacity, display.Opacity);

            material.SetInteger(Props.SizeMode, (int)display.SizeMode);
            material.SetFloat(Props.PointPixelSize, display.PixelSize);
            material.SetFloat(Props.PointWorldRadius, display.WorldRadius);
            material.SetFloat(Props.MinPixelSize, display.MinPixelSize);
            material.SetFloat(Props.MaxPixelSize, display.MaxPixelSize);

            material.SetInteger(Props.ColorIsSRGB, descriptor.ColorIsLinear ? 0 : 1);
            material.SetMatrix(Props.CloudToWorld, cloud.CloudToWorld);
            material.SetInteger(Props.PointCount, cloud.UploadedPointCount);

            SetKeyword(material, HasColorKeyword, cloud.Has(PointAttributes.Color));
            SetKeyword(material, HasNormalKeyword, cloud.Has(PointAttributes.Normal));
            SetKeyword(material, ShapeCircleKeyword, display.Shape == PointShape.Circle);
        }

        /// <summary>
        /// Re-point the generic scalar slot after the caller changes a cloud's colour mode or
        /// scalar slot. Also happens automatically each frame, but calling it explicitly means
        /// a mode change takes effect immediately rather than one frame later.
        /// </summary>
        public void RefreshScalarBinding(GpuPointCloud cloud)
        {
            if (cloud != null) SelectScalarSource(cloud);
        }

        /// <summary>
        /// Point the generic scalar slot at whatever the active mode reads. Done here rather
        /// than in the UI so a mode change is a single enum assignment from the caller's side.
        /// </summary>
        void SelectScalarSource(GpuPointCloud cloud)
        {
            var attribute = cloud.Display.ColorMode switch
            {
                PointColorMode.Intensity  => PointAttributes.Intensity,
                PointColorMode.Confidence => PointAttributes.Confidence,
                PointColorMode.Label      => PointAttributes.Label,
                PointColorMode.Timestamp  => PointAttributes.Timestamp,
                PointColorMode.Scalar     => PointAttributeInfo.ScalarSlot(cloud.Display.ScalarSlot),
                _                         => PointAttributes.None,
            };

            if (attribute != PointAttributes.None) cloud.BindScalarSlot(attribute);
        }

        /// <summary>
        /// Fit the colour range to what is actually on screen for the camera-relative modes.
        /// Transforming the eight corners of the cloud's AABB into view space costs
        /// microseconds; the exponential smoothing is what stops the ramp strobing as the
        /// camera moves.
        /// </summary>
        void AutoFitRange(GpuPointCloud cloud, Camera camera)
        {
            var display = cloud.Display;
            if (display.ColorMode is not (PointColorMode.ViewDepth or PointColorMode.RadialDistance))
            {
                SelectScalarSource(cloud);
                return;
            }

            var bounds = cloud.WorldBounds;
            var view   = camera.worldToCameraMatrix;
            Vector3 center = bounds.center, extents = bounds.extents;
            Vector3 cameraPosition = camera.transform.position;

            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
            for (int corner = 0; corner < 8; corner++)
            {
                var world = center + new Vector3(
                    (corner & 1) == 0 ? -extents.x : extents.x,
                    (corner & 2) == 0 ? -extents.y : extents.y,
                    (corner & 4) == 0 ? -extents.z : extents.z);

                float value = display.ColorMode == PointColorMode.ViewDepth
                    ? -view.MultiplyPoint3x4(world).z
                    : Vector3.Distance(world, cameraPosition);

                lo = Mathf.Min(lo, value);
                hi = Mathf.Max(hi, value);
            }

            lo = Mathf.Max(lo, camera.nearClipPlane);
            if (hi <= lo) hi = lo + 1f;

            const float smoothing = 0.25f;
            display.ScalarRange = new float2(
                Mathf.Lerp(display.ScalarRange.x, lo, smoothing),
                Mathf.Lerp(display.ScalarRange.y, hi, smoothing));
        }

        static void SetKeyword(Material material, string keyword, bool enabled)
        {
            if (enabled) material.EnableKeyword(keyword);
            else material.DisableKeyword(keyword);
        }

        /// <summary>Distinct, readable overlay colours for the first handful of clouds.</summary>
        static Color OverlayColor(int index)
        {
            float hue = Mathf.Repeat(index * 0.6180339887f + 0.08f, 1f);
            return Color.HSVToRGB(hue, 0.62f, 1f);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Clear();
            _colormaps?.Dispose();
        }
    }
}
