using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PointCloud.App.Bootstrap;
using PointCloud.App.CameraControl;
using PointCloud.App.Input;
using PointCloud.App.UI;
using PointCloud.Core.Data;
using PointCloud.Core.Sources;
using PointCloud.Core.Synthetic;
using PointCloud.Rendering;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace PointCloud.App.Viewer
{
    /// <summary>
    /// Orchestrates one viewport: renderer, camera, input, UI and file loading.
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

        /// <summary>Frames own their point data; disposing a frame frees it at refcount zero.</summary>
        readonly List<PointCloudFrame> _frames = new();
        readonly List<Bounds> _boundsScratch = new();

        CancellationTokenSource _loadCancellation;
        bool _loading;

        SourceUpAxis _upAxis = SourceUpAxis.ZUp;
        bool _pointerStartedOverUi;

        public PointCloudRenderer Renderer => _renderer;
        public OrbitFlyController Controller => _controller;
        public bool IsLoading => _loading;

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
                _ui.ZoomSensitivityChanged  += rate => _controller.ZoomSensitivity = rate;
                _ui.ZeroSelectedRequested   += () => ZeroPosition(_ui.SelectedCloud);
                _ui.ZeroAllRequested        += ZeroAllPositions;
                _ui.ResetPositionsRequested += ResetPositions;
                _ui.OpenDialogRequested     += ShowOpenDialog;
                _ui.FilesRequested          += paths => _ = OpenFilesAsync(paths);
                _ui.LoadCancelRequested     += CancelLoad;
                _ui.ClearRequested          += ClearClouds;
                _services?.Own(_ui);

                RefreshRecentFiles();
            }

            // Files named on the command line, so `viz.exe cloud.ply` and "Open with…" work.
            var commandLineFiles = CommandLineFiles();
            if (commandLineFiles.Length > 0)
                _ = OpenFilesAsync(commandLineFiles);
            else if (_generateOnStart)
                LoadSynthetic(_shape, _pointCount, _scale);
        }

        // --- loading -------------------------------------------------------------

        void ShowOpenDialog()
        {
            var dialog = _services?.FileDialog;
            if (dialog == null || !dialog.IsAvailable)
            {
                _ui?.SetStatus("No file dialog on this platform — paste a path into the Path field.", true);
                return;
            }

            var extensions = _services.Registry.SupportedExtensions;
            var paths = dialog.OpenFiles("Open point cloud", extensions,
                                         allowMultiple: true, _services.Recent.LastDirectory);

            if (paths.Length > 0) _ = OpenFilesAsync(paths);
        }

        /// <summary>
        /// Load one or more files, adding them to the scene rather than replacing what is
        /// already there — overlaying a prediction on its ground truth is the core comparison
        /// this tool exists for.
        /// </summary>
        public async Task OpenFilesAsync(string[] paths)
        {
            if (paths == null || paths.Length == 0 || _loading) return;

            _loading = true;
            _loadCancellation?.Dispose();
            _loadCancellation = new CancellationTokenSource();

            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    if (_loadCancellation.IsCancellationRequested) break;

                    string path = paths[i];
                    string prefix = paths.Length > 1 ? $"[{i + 1}/{paths.Length}] " : "";
                    _ui?.SetStatus($"{prefix}Loading {Path.GetFileName(path)}…");

                    // Progress<T> captures the current synchronisation context, so these
                    // callbacks arrive on the main thread and can touch the UI directly.
                    var progress = new Progress<LoadProgress>(report =>
                    {
                        _ui?.ShowProgress(report);
                        _ui?.SetStatus($"{prefix}{Path.GetFileName(path)} — {report}");
                    });

                    var result = await _services.Loader
                        .LoadAsync(path, FrameRequest.Default, progress, _loadCancellation.Token)
                        .ConfigureAwait(true);   // resume on the main thread; GPU upload needs it

                    if (result.Cancelled)
                    {
                        _ui?.SetStatus("Load cancelled.");
                        break;
                    }

                    if (!result.Succeeded)
                    {
                        _ui?.SetStatus(result.UserMessage, true);
                        continue;
                    }

                    AddFrame(result.Frame);
                    _services.Recent.Add(path);
                    _ui?.SetStatus($"{Path.GetFileName(path)} — {result.Frame.Data.Descriptor.PointCount:N0} points " +
                                   $"in {result.ElapsedMs:F0} ms");
                }
            }
            catch (Exception e)
            {
                _ui?.SetStatus($"Load failed: {e.Message}", true);
                Log($"Load failed: {e}");
            }
            finally
            {
                _loading = false;
                _ui?.ShowProgress(null);
                RefreshRecentFiles();
                FrameAll();
            }
        }

        void CancelLoad() => _loadCancellation?.Cancel();

        void RefreshRecentFiles()
        {
            if (_services == null) return;
            _services.Recent.PruneMissing();
            _ui?.SetRecentFiles(_services.Recent.Paths);
        }

        /// <summary>
        /// Point-cloud files named on the command line.
        ///
        /// Matched by supported extension, not merely by "this path exists". Command lines
        /// are full of paths that belong to a preceding flag — Unity's own -logFile and
        /// -testResults among them — and accepting any existing file means the app opens a
        /// log instead of a cloud.
        /// </summary>
        string[] CommandLineFiles()
        {
            var extensions = _services?.Registry?.SupportedExtensions;
            if (extensions == null || extensions.Length == 0) return Array.Empty<string>();

            var files = new List<string>();
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                if (string.IsNullOrWhiteSpace(argument) || argument.StartsWith("-")) continue;

                var extension = Path.GetExtension(argument);
                bool supported = false;
                foreach (var candidate in extensions)
                    if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase)) { supported = true; break; }

                if (supported && File.Exists(argument)) files.Add(argument);
            }
            return files.ToArray();
        }

        // --- clouds --------------------------------------------------------------

        public GpuPointCloud LoadSynthetic(SyntheticShape shape, int pointCount, float scale = 10f)
        {
            var settings = SyntheticCloudSettings.Default(shape, pointCount);
            settings.Scale = scale;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var data = SyntheticCloudGenerator.Generate(settings, Allocator.Persistent);
            stopwatch.Stop();

            Log($"Generated {data.Descriptor} in {stopwatch.ElapsedMilliseconds} ms " +
                $"({data.Chunks.Length} chunks, {data.Descriptor.EstimatedBytes / (1024 * 1024)} MB)");

            // Replace on synthetic generation: it is a scratch cloud, not something you
            // overlay a real scan against.
            ClearClouds();
            return AddFrame(new PointCloudFrame(data));
        }

        /// <summary>Take ownership of a loaded frame, upload it, and make it visible.</summary>
        public GpuPointCloud AddFrame(PointCloudFrame frame)
        {
            _frames.Add(frame);
            var cloud = _renderer.Add(frame.Data);

            // Applied here rather than in Rendering, which must never reference App — that
            // dependency direction is what keeps the renderer reusable and a VRS source a
            // drop-in later.
            cloud.Display.FlatColor = UiPalette.FlatPointColor;

            if (!frame.Data.Descriptor.OrientationIsAuthoritative)
                ApplyUpAxisTo(cloud, _upAxis);

            _ui?.RefreshCloudList();
            return cloud;
        }

        public void ClearClouds()
        {
            _renderer?.Clear();
            foreach (var frame in _frames) frame.Dispose();
            _frames.Clear();
            _ui?.RefreshCloudList();
        }

        void FrameAll()
        {
            if (_renderer.Clouds.Count > 0 && TryGetVisibleBounds(out var bounds))
                _controller.Frame(_camera, bounds, animate: false);
        }

        // --- per-frame -----------------------------------------------------------

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
        /// Distance along a cursor ray to the nearest visible cloud, for zoom-to-cursor and
        /// for scaling the zoom step.
        ///
        /// Tests chunk AABBs rather than the whole-cloud box. The chunk table is already
        /// resident for culling, and on a sparse or hollow cloud the outer box can sit tens
        /// of metres in front of anything you can actually see — which makes zoom feel like
        /// it stops short. Falls back to the whole-cloud bounds only if a cloud has no chunk
        /// table. M7's CPU picker upgrades this to a true per-point hit.
        /// </summary>
        float? ProbeDepth(Ray ray)
        {
            float nearest = float.PositiveInfinity;

            foreach (var cloud in _renderer.Clouds)
            {
                if (!cloud.Display.Visible) continue;

                if (cloud.TryRaycastChunks(ray, out float distance))
                {
                    if (distance < nearest) nearest = distance;
                }
                else if (cloud.WorldBounds.IntersectRay(ray, out float boundsDistance) &&
                         boundsDistance < nearest)
                {
                    nearest = boundsDistance;
                }
            }

            return float.IsPositiveInfinity(nearest) ? null : nearest;
        }

        // --- placement -----------------------------------------------------------

        /// <summary>
        /// Move a cloud's centre onto the world origin. Applying it to several clouds brings
        /// them into the same place so they can be compared by overlaying.
        /// </summary>
        public void ZeroPosition(GpuPointCloud cloud)
        {
            if (cloud == null) return;

            cloud.CenterAtOrigin();
            Log($"'{cloud.Descriptor.Name}' centred at the origin (offset {cloud.Translation}).");
            _ui?.RefreshCloudList();
        }

        /// <summary>Centre every loaded cloud at the origin — the alignment shortcut for comparison.</summary>
        public void ZeroAllPositions()
        {
            foreach (var cloud in _renderer.Clouds) cloud.CenterAtOrigin();

            Log($"Centred {_renderer.Clouds.Count} cloud(s) at the origin.");
            _ui?.RefreshCloudList();
            FrameAll();
        }

        /// <summary>Restore every cloud to its source position.</summary>
        public void ResetPositions()
        {
            foreach (var cloud in _renderer.Clouds) cloud.ResetTransform();

            Log("Restored source positions.");
            _ui?.RefreshCloudList();
            FrameAll();
        }

        void ApplyUpAxis(SourceUpAxis upAxis)
        {
            _upAxis = upAxis;

            foreach (var cloud in _renderer.Clouds)
            {
                if (cloud.Descriptor.OrientationIsAuthoritative) continue;
                ApplyUpAxisTo(cloud, upAxis);
            }

            FrameAll();
        }

        static void ApplyUpAxisTo(GpuPointCloud cloud, SourceUpAxis upAxis)
        {
            // Only the base conversion changes; any user translation is preserved, so
            // re-orienting a cloud does not silently undo an alignment.
            cloud.BaseTransform = CoordinateConvention.SourceToWorld(upAxis);
            cloud.ApplyTransform();
        }

        /// <summary>
        /// Drop CPU streams once a cloud is fully resident. Positions stay: the picker and
        /// the spatial index both need them, and they are the same 12 B/pt either way.
        /// </summary>
        void ReleaseUploadedCpuStreams()
        {
            foreach (var frame in _frames)
            {
                if (!frame.IsAlive) continue;
                var data = frame.Data;
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

        void OnDestroy()
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;

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
