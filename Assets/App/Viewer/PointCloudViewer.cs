using System.Collections.Generic;
using PointCloud.App.Bootstrap;
using PointCloud.App.CameraControl;
using PointCloud.App.Input;
using PointCloud.App.UI;
using PointCloud.Core.Data;
using PointCloud.Core.Synthetic;
using PointCloud.Rendering;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace PointCloud.App.Viewer
{
    /// <summary>
    /// Orchestrates one viewport: renderer, camera, input and UI.
    ///
    /// The only MonoBehaviour in the runtime path. Everything it drives is a plain class,
    /// so the pieces stay testable and lifetimes stay explicit — which matters here because
    /// domain reload is disabled and anything holding a NativeArray or GraphicsBuffer that
    /// escapes disposal survives into the next play session.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class PointCloudViewer : MonoBehaviour
    {
        [SerializeField] Camera _camera;
        [SerializeField] UIDocument _uiDocument;

        [Header("Startup cloud")]
        [SerializeField] bool           _generateOnStart = true;
        [SerializeField] SyntheticShape _shape = SyntheticShape.Terrain;
        [SerializeField] int            _pointCount = 1_000_000;
        [SerializeField] float          _scale = 10f;

        PointCloudRenderer  _renderer;
        OrbitFlyController  _controller;
        ViewportInput       _input;
        ViewerUi            _ui;
        AppServices         _services;

        readonly List<PointCloudData> _loaded = new();
        readonly List<Bounds> _boundsScratch = new();

        SourceUpAxis _upAxis = SourceUpAxis.ZUp;
        bool _pointerStartedOverUi;

        public PointCloudRenderer Renderer => _renderer;
        public OrbitFlyController Controller => _controller;

        void Awake()
        {
            if (_camera == null) _camera = Camera.main;
            _services = FindAnyObjectByType<AppBootstrap>()?.Services;

            _input      = new ViewportInput();
            _controller = new OrbitFlyController();
        }

        void Start()
        {
            // Enforce the palette at runtime as well as in the saved scene, so changing
            // UiPalette does not require rebuilding Main.unity to take effect.
            if (_camera != null)
            {
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = UiPalette.SceneBackground;
            }

            _renderer = new PointCloudRenderer();
            _services?.Own(_renderer);

            if (_uiDocument != null)
            {
                _ui = new ViewerUi(_uiDocument, _renderer);
                _ui.SyntheticCloudRequested += (shape, count) => LoadSynthetic(shape, count, _scale);
                _ui.UpAxisChanged           += ApplyUpAxis;
                _ui.ZoomToCursorChanged     += enabled => _controller.ZoomToCursor = enabled;
                _services?.Own(_ui);
            }

            if (_generateOnStart)
                LoadSynthetic(_shape, _pointCount, _scale);
        }

        public GpuPointCloud LoadSynthetic(SyntheticShape shape, int pointCount, float scale = 10f)
        {
            var settings = SyntheticCloudSettings.Default(shape, pointCount);
            settings.Scale = scale;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
            stopwatch.Stop();

            Log($"Generated {data.Descriptor} in {stopwatch.ElapsedMilliseconds} ms " +
                $"({data.Chunks.Length} chunks, {data.Descriptor.EstimatedBytes / (1024 * 1024)} MB)");

            return Adopt(data);
        }

        /// <summary>Take ownership of loaded data, upload it, and frame it.</summary>
        public GpuPointCloud Adopt(PointCloudData data)
        {
            ClearClouds();

            _loaded.Add(data);
            var cloud = _renderer.Add(data);

            // Applied here rather than in Rendering, which must never reference App — that
            // dependency direction is what keeps the renderer reusable and a VRS source a
            // drop-in later.
            cloud.Display.FlatColor = UiPalette.FlatPointColor;

            if (!data.Descriptor.OrientationIsAuthoritative)
                ApplyUpAxisTo(cloud, _upAxis);

            _ui?.RefreshCloudList();
            _controller.Frame(_camera, cloud.WorldBounds, animate: false);

            return cloud;
        }

        void LateUpdate()
        {
            if (_renderer == null) return;

            UpdateInputArbitration();
            HandleHotkeys();

            _controller.Update(_camera, _input, Time.unscaledDeltaTime, ProbeDepth);

            FitClipPlanes();

            _renderer.Render(_camera);
            _ui?.UpdateStats(Time.unscaledDeltaTime);

            ReleaseUploadedCpuStreams();
        }

        /// <summary>
        /// Decide once, on pointer-down, whether this drag belongs to the UI.
        ///
        /// Testing every frame instead would hand the drag back and forth as the cursor
        /// crosses a panel edge, which reads as a stutter. The latch releases only when every
        /// button is up.
        /// </summary>
        void UpdateInputArbitration()
        {
            if (_ui == null || !_input.HasMouse) return;

            bool anyHeld = _input.LeftHeld || _input.MiddleHeld || _input.RightHeld;

            if (_input.AnyButtonPressedThisFrame && !_pointerStartedOverUi)
                _pointerStartedOverUi = _ui.IsPointerOverUi(_input.PointerPosition);
            else if (!anyHeld)
                _pointerStartedOverUi = false;

            // While no button is down, hovering a panel should still block the scroll wheel.
            _input.PointerCapturedByUi = anyHeld
                ? _pointerStartedOverUi
                : _ui.IsPointerOverUi(_input.PointerPosition);
        }

        void HandleHotkeys()
        {
            if (_input.PointerCapturedByUi) return;

            if (_input.FocusSelectedPressed)
            {
                var selected = _ui?.SelectedCloud ?? (_renderer.Clouds.Count > 0 ? _renderer.Clouds[0] : null);
                if (selected != null) _controller.Frame(_camera, selected.WorldBounds);
            }

            if (_input.FocusAllPressed && TryGetVisibleBounds(out var all))
                _controller.Frame(_camera, all);

            // Any manual view change abandons an in-flight framing animation.
            if (_input.OrbitActive || _input.PanActive || _input.DollyActive)
                _controller.CancelFraming();
        }

        void FitClipPlanes()
        {
            bool hasBounds = TryGetVisibleBounds(out var bounds);
            ClipPlaneFitter.Fit(_camera, bounds, hasBounds);
        }

        bool TryGetVisibleBounds(out Bounds bounds)
        {
            _boundsScratch.Clear();
            foreach (var cloud in _renderer.Clouds)
                if (cloud.Display.Visible) _boundsScratch.Add(cloud.WorldBounds);

            return ClipPlaneFitter.TryUnion(_boundsScratch, out bounds);
        }

        /// <summary>
        /// Distance along a cursor ray to the nearest visible cloud, for zoom-to-cursor.
        ///
        /// Bounds-level for now, which is enough to make zooming feel anchored. M7's CPU
        /// picker replaces this with a true per-point hit so zooming targets the surface
        /// under the cursor rather than the front of its bounding box.
        /// </summary>
        float? ProbeDepth(Ray ray)
        {
            float nearest = float.PositiveInfinity;

            foreach (var cloud in _renderer.Clouds)
            {
                if (!cloud.Display.Visible) continue;
                if (cloud.WorldBounds.IntersectRay(ray, out float distance) && distance < nearest)
                    nearest = distance;
            }

            return float.IsPositiveInfinity(nearest) ? null : nearest;
        }

        void ApplyUpAxis(SourceUpAxis upAxis)
        {
            _upAxis = upAxis;

            foreach (var cloud in _renderer.Clouds)
            {
                if (cloud.Descriptor.OrientationIsAuthoritative) continue;
                ApplyUpAxisTo(cloud, upAxis);
            }

            if (TryGetVisibleBounds(out var bounds))
                _controller.Frame(_camera, bounds);
        }

        static void ApplyUpAxisTo(GpuPointCloud cloud, SourceUpAxis upAxis)
        {
            cloud.CloudToWorld = CoordinateConvention.SourceToWorld(upAxis);
            cloud.Material.SetMatrix(GpuPointCloud.Props.CloudToWorld, cloud.CloudToWorld);
            cloud.RecomputeWorldBounds();
        }

        /// <summary>
        /// Drop CPU streams once a cloud is fully resident. Positions stay: the picker and
        /// the spatial index both need them, and they are the same 12 B/pt either way.
        /// </summary>
        void ReleaseUploadedCpuStreams()
        {
            for (int i = 0; i < _loaded.Count; i++)
            {
                var data = _loaded[i];
                if (data.Retention == CpuRetention.PositionsOnly) continue;

                var cloud = FindCloudFor(data);
                if (cloud == null || !cloud.IsFullyUploaded) continue;

                data.ApplyRetention(CpuRetention.PositionsOnly);
                Log($"'{data.Descriptor.Name}' resident — {cloud.VramBytes / (1024 * 1024)} MB in VRAM, " +
                    "CPU streams reduced to positions.");
            }
        }

        GpuPointCloud FindCloudFor(PointCloudData data)
        {
            foreach (var cloud in _renderer.Clouds)
                if (ReferenceEquals(cloud.Descriptor, data.Descriptor)) return cloud;
            return null;
        }

        void ClearClouds()
        {
            _renderer.Clear();
            foreach (var data in _loaded) data.Dispose();
            _loaded.Clear();
        }

        void OnDestroy()
        {
            if (_renderer != null) ClearClouds();

            // AppServices owns the renderer and UI when one exists; dispose directly only
            // when it does not, so nothing is disposed twice.
            if (_services == null)
            {
                _renderer?.Dispose();
                _ui?.Dispose();
            }

            _renderer = null;
            _ui = null;
        }

        void Log(string message)
        {
            if (_services != null) _services.Log.Info("Viewer", message);
            else Debug.Log($"[Viewer] {message}");
        }
    }
}
