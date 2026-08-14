using System;
using System.Collections.Generic;
using System.Linq;
using PointCloud.Core.Data;
using PointCloud.Core.Sources;
using PointCloud.Core.Synthetic;
using PointCloud.Rendering;
using UnityEngine;
using UnityEngine.UIElements;

namespace PointCloud.App.UI
{
    /// <summary>
    /// Binds the runtime UI Toolkit document to the renderer.
    ///
    /// Everything here reflects one principle: a CV engineer needs to know what their file
    /// does and does not contain. Unsupported render modes stay visible and say why they
    /// are unavailable, and absent attributes are listed and marked rather than omitted —
    /// "intensity is missing" and "intensity is unavailable for some reason" send someone
    /// down completely different debugging paths.
    /// </summary>
    public sealed class ViewerUi : IDisposable
    {
        static readonly int[] SyntheticCounts = { 100_000, 1_000_000, 5_000_000, 20_000_000, 50_000_000 };

        readonly UIDocument _document;
        readonly PointCloudRenderer _renderer;

        VisualElement   _root;
        ListView        _cloudList;
        VisualElement   _attributeList;
        Label           _sourceInfo;
        DropdownField   _colorMode, _colormap, _sizeMode, _shape, _upAxis, _syntheticShape, _syntheticCount;
        Label           _colorModeHint;
        Slider          _pixelSize, _opacity;
        Toggle          _zoomToCursor;
        Button          _loadSynthetic, _openFile, _clearClouds, _cancelLoad;
        Button          _zeroPosition, _zeroAll, _resetPositions;
        Slider          _zoomSensitivity;
        DropdownField   _recentFiles;
        TextField       _pathField;
        Label           _loadStatus;
        ProgressBar     _loadProgress;
        Label           _statPoints, _statDrawn, _statDraws, _statVram, _statFrame;

        readonly List<GpuPointCloud> _cloudItems = new();
        readonly PointColorMode[] _colorModes = (PointColorMode[])Enum.GetValues(typeof(PointColorMode));

        bool _suppressCallbacks;
        float _smoothedFrameMs;

        /// <summary>Raised when the user asks for a synthetic cloud: (shape, point count).</summary>
        public event Action<SyntheticShape, int> SyntheticCloudRequested;

        /// <summary>Raised when the user picks or types a file to open.</summary>
        public event Action<string[]> FilesRequested;

        /// <summary>Raised when the user asks to show the open dialog.</summary>
        public event Action OpenDialogRequested;

        /// <summary>Raised when the user cancels an in-flight load.</summary>
        public event Action LoadCancelRequested;

        /// <summary>Raised when the user asks to remove every loaded cloud.</summary>
        public event Action ClearRequested;

        /// <summary>Raised when the up-axis convention changes and clouds must be re-oriented.</summary>
        public event Action<SourceUpAxis> UpAxisChanged;

        /// <summary>Raised when the user toggles zoom-to-cursor.</summary>
        public event Action<bool> ZoomToCursorChanged;

        /// <summary>Raised when the user changes the zoom rate.</summary>
        public event Action<float> ZoomSensitivityChanged;

        /// <summary>Raised to centre the selected cloud on the world origin.</summary>
        public event Action ZeroSelectedRequested;

        /// <summary>Raised to centre every cloud on the world origin.</summary>
        public event Action ZeroAllRequested;

        /// <summary>Raised to restore every cloud's source position.</summary>
        public event Action ResetPositionsRequested;

        public GpuPointCloud SelectedCloud { get; private set; }

        public ViewerUi(UIDocument document, PointCloudRenderer renderer)
        {
            _document = document != null ? document : throw new ArgumentNullException(nameof(document));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            Build();
        }

        void Build()
        {
            _root = _document.rootVisualElement;
            if (_root == null) return;

            _statPoints = _root.Q<Label>("stat-points");
            _statDrawn  = _root.Q<Label>("stat-drawn");
            _statDraws  = _root.Q<Label>("stat-draws");
            _statVram   = _root.Q<Label>("stat-vram");
            _statFrame  = _root.Q<Label>("stat-frame");

            _cloudList     = _root.Q<ListView>("cloud-list");
            _attributeList = _root.Q<VisualElement>("attribute-list");
            _sourceInfo    = _root.Q<Label>("source-info");

            _colorMode     = _root.Q<DropdownField>("color-mode");
            _colorModeHint = _root.Q<Label>("color-mode-hint");
            _colormap      = _root.Q<DropdownField>("colormap");
            _sizeMode      = _root.Q<DropdownField>("size-mode");
            _shape         = _root.Q<DropdownField>("shape");
            _pixelSize     = _root.Q<Slider>("pixel-size");
            _opacity       = _root.Q<Slider>("opacity");
            _upAxis        = _root.Q<DropdownField>("up-axis");
            _zoomToCursor  = _root.Q<Toggle>("zoom-to-cursor");

            _syntheticShape = _root.Q<DropdownField>("synthetic-shape");
            _syntheticCount = _root.Q<DropdownField>("synthetic-count");
            _loadSynthetic  = _root.Q<Button>("load-synthetic");

            _zeroPosition    = _root.Q<Button>("zero-position");
            _zeroAll         = _root.Q<Button>("zero-all");
            _resetPositions  = _root.Q<Button>("reset-positions");
            _zoomSensitivity = _root.Q<Slider>("zoom-sensitivity");

            _openFile     = _root.Q<Button>("open-file");
            _clearClouds  = _root.Q<Button>("clear-clouds");
            _cancelLoad   = _root.Q<Button>("cancel-load");
            _recentFiles  = _root.Q<DropdownField>("recent-files");
            _pathField    = _root.Q<TextField>("path-field");
            _loadStatus   = _root.Q<Label>("load-status");
            _loadProgress = _root.Q<ProgressBar>("load-progress");

            SetupCloudList();
            SetupDropdowns();
            SetupSliders();
            SetupOpenControls();
        }

        void SetupOpenControls()
        {
            if (_zeroPosition != null) _zeroPosition.clicked += () => ZeroSelectedRequested?.Invoke();
            if (_zeroAll != null) _zeroAll.clicked += () => ZeroAllRequested?.Invoke();
            if (_resetPositions != null) _resetPositions.clicked += () => ResetPositionsRequested?.Invoke();

            if (_zoomSensitivity != null)
            {
                _zoomSensitivity.SetValueWithoutNotify(0.35f);
                _zoomSensitivity.RegisterValueChangedCallback(evt =>
                {
                    if (!_suppressCallbacks) ZoomSensitivityChanged?.Invoke(evt.newValue);
                });
            }

            if (_openFile != null) _openFile.clicked += () => OpenDialogRequested?.Invoke();
            if (_clearClouds != null) _clearClouds.clicked += () => ClearRequested?.Invoke();
            if (_cancelLoad != null) _cancelLoad.clicked += () => LoadCancelRequested?.Invoke();

            // A typed or pasted path is the fallback when no native dialog exists, and the
            // fastest route when the user already has the path on their clipboard.
            if (_pathField != null)
            {
                _pathField.RegisterCallback<KeyDownEvent>(evt =>
                {
                    if (evt.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter)) return;

                    var path = _pathField.value?.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(path)) FilesRequested?.Invoke(new[] { path });
                    evt.StopPropagation();
                });
            }

            if (_recentFiles != null)
            {
                _recentFiles.RegisterValueChangedCallback(_ =>
                {
                    if (_suppressCallbacks) return;
                    int index = _recentFiles.index;
                    if (index >= 0 && index < _recentPaths.Count)
                        FilesRequested?.Invoke(new[] { _recentPaths[index] });
                });
            }

            ShowProgress(null);
        }

        readonly List<string> _recentPaths = new();

        /// <summary>Refresh the recent list. Full paths are the tooltip; the label is the file name.</summary>
        public void SetRecentFiles(IReadOnlyList<string> paths)
        {
            _recentPaths.Clear();
            if (paths != null) _recentPaths.AddRange(paths);

            if (_recentFiles == null) return;

            _suppressCallbacks = true;
            _recentFiles.choices = _recentPaths.Select(System.IO.Path.GetFileName).ToList();
            _recentFiles.index = -1;
            _recentFiles.SetEnabled(_recentPaths.Count > 0);
            _suppressCallbacks = false;
        }

        /// <summary>Show load progress, or pass null to hide the progress controls entirely.</summary>
        public void ShowProgress(LoadProgress? progress)
        {
            bool active = progress.HasValue &&
                          progress.Value.Phase is not (LoadPhase.Complete or LoadPhase.Failed);

            if (_loadProgress != null)
            {
                _loadProgress.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;
                if (active)
                {
                    float fraction = progress.Value.Fraction;
                    // A phase with no measurable total still shows the phase name rather than
                    // a bar frozen at zero, which reads as a hang.
                    _loadProgress.value = fraction >= 0f ? fraction : 0f;
                    _loadProgress.title = fraction >= 0f
                        ? $"{progress.Value.Phase} {fraction * 100f:F0}%"
                        : progress.Value.Phase.ToString();
                }
            }

            if (_cancelLoad != null)
                _cancelLoad.style.display = active ? DisplayStyle.Flex : DisplayStyle.None;

            if (_openFile != null) _openFile.SetEnabled(!active);
        }

        /// <summary>One-line status under the open controls. Errors are styled as a callout.</summary>
        public void SetStatus(string message, bool isError = false)
        {
            if (_loadStatus == null) return;

            _loadStatus.text = message ?? string.Empty;
            _loadStatus.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;

            _loadStatus.RemoveFromClassList("pc-hint");
            _loadStatus.RemoveFromClassList("pc-warning");
            _loadStatus.AddToClassList(isError ? "pc-warning" : "pc-hint");
        }

        void SetupCloudList()
        {
            if (_cloudList == null) return;

            _cloudList.itemsSource = _cloudItems;
            _cloudList.fixedItemHeight = 34f;
            _cloudList.selectionType = SelectionType.Single;

            _cloudList.makeItem = () =>
            {
                var row = new VisualElement();
                row.AddToClassList("pc-cloud-item");

                var swatch = new VisualElement { name = "swatch" };
                swatch.AddToClassList("pc-cloud-item__swatch");

                var visible = new Toggle { name = "visible" };

                var text = new VisualElement { name = "text", style = { flexGrow = 1 } };
                var name = new Label { name = "name" };
                name.AddToClassList("pc-cloud-item__name");
                var meta = new Label { name = "meta" };
                meta.AddToClassList("pc-cloud-item__meta");
                text.Add(name);
                text.Add(meta);

                row.Add(visible);
                row.Add(swatch);
                row.Add(text);
                return row;
            };

            _cloudList.bindItem = (element, index) =>
            {
                if (index < 0 || index >= _cloudItems.Count) return;
                var cloud = _cloudItems[index];

                element.Q<Label>("name").text = cloud.Descriptor.Name;

                // Flag a moved cloud: a silently-shifted position would be badly misleading
                // when the whole purpose of the view is comparing two clouds' geometry.
                string placement = cloud.IsTranslated ? " · zeroed" : "";
                element.Q<Label>("meta").text =
                    $"{FormatCount(cloud.Descriptor.PointCount)} · {cloud.Descriptor.FormatId} · " +
                    $"{cloud.VramBytes / (1024 * 1024)} MB{placement}";

                element.Q<VisualElement>("swatch").style.backgroundColor = cloud.Display.CloudColor;

                var visible = element.Q<Toggle>("visible");
                visible.SetValueWithoutNotify(cloud.Display.Visible);
                visible.UnregisterCallback<ChangeEvent<bool>, GpuPointCloud>(OnVisibleToggled);
                visible.RegisterCallback<ChangeEvent<bool>, GpuPointCloud>(OnVisibleToggled, cloud);
            };

            _cloudList.selectionChanged += selection =>
            {
                SelectedCloud = selection.FirstOrDefault() as GpuPointCloud;
                RefreshForSelection();
            };
        }

        static void OnVisibleToggled(ChangeEvent<bool> evt, GpuPointCloud cloud) =>
            cloud.Display.Visible = evt.newValue;

        void SetupDropdowns()
        {
            if (_colormap != null)
            {
                _colormap.choices = ColormapLibrary.Names.ToList();
                _colormap.index = (int)Colormap.Turbo;
                _colormap.RegisterValueChangedCallback(_ =>
                {
                    if (_suppressCallbacks || SelectedCloud == null) return;
                    SelectedCloud.Display.Colormap = (Colormap)_colormap.index;
                });
            }

            if (_colorMode != null)
            {
                _colorMode.RegisterValueChangedCallback(_ => OnColorModeChanged());
            }

            BindEnumDropdown<PointSizeMode>(_sizeMode, value =>
            {
                if (SelectedCloud != null) SelectedCloud.Display.SizeMode = value;
            });

            BindEnumDropdown<PointShape>(_shape, value =>
            {
                if (SelectedCloud != null) SelectedCloud.Display.Shape = value;
            });

            if (_upAxis != null)
            {
                _upAxis.choices = new List<string> { "Z-up (sensor data)", "Y-up (Unity / DCC)" };
                _upAxis.index = 0;
                _upAxis.RegisterValueChangedCallback(_ =>
                {
                    if (_suppressCallbacks) return;
                    UpAxisChanged?.Invoke(_upAxis.index == 0 ? SourceUpAxis.ZUp : SourceUpAxis.YUp);
                });
            }

            if (_zoomToCursor != null)
            {
                _zoomToCursor.SetValueWithoutNotify(true);
                _zoomToCursor.RegisterValueChangedCallback(evt =>
                {
                    if (!_suppressCallbacks) ZoomToCursorChanged?.Invoke(evt.newValue);
                });
            }

            if (_syntheticShape != null)
            {
                _syntheticShape.choices = Enum.GetNames(typeof(SyntheticShape)).ToList();
                _syntheticShape.index = 0;
            }

            if (_syntheticCount != null)
            {
                _syntheticCount.choices = SyntheticCounts.Select(FormatCount).ToList();
                _syntheticCount.index = 1;
            }

            if (_loadSynthetic != null)
            {
                _loadSynthetic.clicked += () =>
                {
                    var shape = (SyntheticShape)Mathf.Max(0, _syntheticShape?.index ?? 0);
                    int count = SyntheticCounts[Mathf.Clamp(_syntheticCount?.index ?? 1, 0, SyntheticCounts.Length - 1)];
                    SyntheticCloudRequested?.Invoke(shape, count);
                };
            }
        }

        void BindEnumDropdown<T>(DropdownField field, Action<T> onChanged) where T : struct, Enum
        {
            if (field == null) return;

            field.choices = Enum.GetNames(typeof(T)).Select(Humanize).ToList();
            field.index = 0;
            field.RegisterValueChangedCallback(_ =>
            {
                if (_suppressCallbacks) return;
                var values = (T[])Enum.GetValues(typeof(T));
                if (field.index >= 0 && field.index < values.Length) onChanged(values[field.index]);
            });
        }

        void SetupSliders()
        {
            if (_pixelSize != null)
            {
                _pixelSize.SetValueWithoutNotify(3f);
                _pixelSize.RegisterValueChangedCallback(evt =>
                {
                    if (_suppressCallbacks || SelectedCloud == null) return;
                    SelectedCloud.Display.PixelSize = evt.newValue;
                    SelectedCloud.Display.MaxPixelSize = Mathf.Max(evt.newValue, 8f);
                });
            }

            if (_opacity != null)
            {
                _opacity.SetValueWithoutNotify(1f);
                _opacity.RegisterValueChangedCallback(evt =>
                {
                    if (_suppressCallbacks || SelectedCloud == null) return;
                    SelectedCloud.Display.Opacity = evt.newValue;
                });
            }
        }

        /// <summary>
        /// Rebuild the mode list for the selected cloud. Unsupported modes stay in the list,
        /// suffixed with why — hiding them would leave the user wondering whether the tool
        /// or their data is at fault.
        /// </summary>
        void RebuildColorModes()
        {
            if (_colorMode == null) return;

            var descriptor = SelectedCloud?.Descriptor;
            var choices = new List<string>(_colorModes.Length);

            foreach (var mode in _colorModes)
            {
                bool supported = descriptor == null || PointCloudDisplaySettings.IsSupported(mode, descriptor);
                choices.Add(supported ? Humanize(mode.ToString()) : $"{Humanize(mode.ToString())}  — not in this file");
            }

            _suppressCallbacks = true;
            _colorMode.choices = choices;
            _colorMode.index = SelectedCloud != null ? Array.IndexOf(_colorModes, SelectedCloud.Display.ColorMode) : 0;
            _suppressCallbacks = false;

            UpdateColorModeHint();
        }

        void OnColorModeChanged()
        {
            if (_suppressCallbacks || SelectedCloud == null) return;

            int index = Mathf.Clamp(_colorMode.index, 0, _colorModes.Length - 1);
            var mode = _colorModes[index];

            if (!PointCloudDisplaySettings.IsSupported(mode, SelectedCloud.Descriptor))
            {
                // Revert rather than render a mode with no data behind it, and say why.
                _suppressCallbacks = true;
                _colorMode.index = Array.IndexOf(_colorModes, SelectedCloud.Display.ColorMode);
                _suppressCallbacks = false;
                SetHint(PointCloudDisplaySettings.UnsupportedReason(mode));
                return;
            }

            SelectedCloud.Display.ColorMode = mode;
            _renderer.RefreshScalarBinding(SelectedCloud);
            UpdateColorModeHint();
        }

        void UpdateColorModeHint()
        {
            if (SelectedCloud == null) { SetHint(null); return; }

            SetHint(SelectedCloud.Display.ColorMode == PointColorMode.ViewDepth &&
                    !SelectedCloud.Descriptor.Has(PointAttributes.Color)
                ? "No colour in this file — showing camera-space distance."
                : null);
        }

        void SetHint(string message)
        {
            if (_colorModeHint == null) return;
            _colorModeHint.text = message ?? string.Empty;
            _colorModeHint.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        void RebuildAttributeList()
        {
            if (_attributeList == null) return;
            _attributeList.Clear();

            var descriptor = SelectedCloud?.Descriptor;
            if (descriptor == null)
            {
                var empty = new Label("No cloud selected.");
                empty.AddToClassList("pc-hint");
                _attributeList.Add(empty);
                return;
            }

            foreach (PointAttributes attribute in Enum.GetValues(typeof(PointAttributes)))
            {
                if (attribute is PointAttributes.None or PointAttributes.AnyScalar) continue;
                // Scalar slots are reported by name below rather than as raw slot numbers.
                if ((attribute & PointAttributes.AnyScalar) != 0) continue;

                bool present = descriptor.Has(attribute);
                var row = new VisualElement();
                row.AddToClassList("pc-attribute");
                row.AddToClassList(present ? "pc-attribute--present" : "pc-attribute--absent");

                var mark = new Label(present ? "✓" : "✗");
                mark.AddToClassList("pc-attribute__mark");

                string suffix = present && attribute == PointAttributes.Label && descriptor.LabelValues != null
                    ? $" ({descriptor.LabelValues.Length} values)"
                    : string.Empty;

                var label = new Label(PointAttributeInfo.Name(attribute) + suffix);
                label.AddToClassList("pc-attribute__label");

                row.Add(mark);
                row.Add(label);
                _attributeList.Add(row);
            }

            foreach (var field in descriptor.ScalarFields ?? Array.Empty<ScalarFieldDescriptor>())
            {
                var row = new VisualElement();
                row.AddToClassList("pc-attribute");
                row.AddToClassList("pc-attribute--present");

                var mark = new Label("✓");
                mark.AddToClassList("pc-attribute__mark");
                var label = new Label($"scalar: {field.Name}");
                label.AddToClassList("pc-attribute__label");

                row.Add(mark);
                row.Add(label);
                _attributeList.Add(row);
            }
        }

        void RefreshForSelection()
        {
            RebuildColorModes();
            RebuildAttributeList();

            var descriptor = SelectedCloud?.Descriptor;
            if (_sourceInfo != null)
            {
                _sourceInfo.text = descriptor == null
                    ? "No cloud loaded."
                    : $"{descriptor.SourcePath}\n{descriptor.PointCount:N0} points · {descriptor.BytesPerPoint} B/pt" +
                      (descriptor.DroppedPointCount > 0 ? $"\nDropped {descriptor.DroppedPointCount:N0} invalid points" : "");
            }

            if (SelectedCloud == null) return;

            _suppressCallbacks = true;
            var display = SelectedCloud.Display;
            if (_colormap != null) _colormap.index = (int)display.Colormap;
            if (_sizeMode != null) _sizeMode.index = (int)display.SizeMode;
            if (_shape != null) _shape.index = (int)display.Shape;
            _pixelSize?.SetValueWithoutNotify(display.PixelSize);
            _opacity?.SetValueWithoutNotify(display.Opacity);
            _suppressCallbacks = false;
        }

        /// <summary>Call when clouds are added or removed.</summary>
        public void RefreshCloudList()
        {
            _cloudItems.Clear();
            _cloudItems.AddRange(_renderer.Clouds);
            _cloudList?.RefreshItems();

            if (SelectedCloud == null || !_cloudItems.Contains(SelectedCloud))
            {
                SelectedCloud = _cloudItems.LastOrDefault();
                if (SelectedCloud != null && _cloudList != null)
                    _cloudList.SetSelectionWithoutNotify(new[] { _cloudItems.IndexOf(SelectedCloud) });
                RefreshForSelection();
            }
        }

        public void UpdateStats(float unscaledDeltaTime)
        {
            // Exponential smoothing: a raw per-frame number is unreadable.
            float frameMs = unscaledDeltaTime * 1000f;
            _smoothedFrameMs = _smoothedFrameMs <= 0f ? frameMs : Mathf.Lerp(_smoothedFrameMs, frameMs, 0.1f);

            if (_statPoints != null) _statPoints.text = $"{FormatCount(_renderer.TotalPointCount)} pts";
            if (_statDrawn != null)  _statDrawn.text  = $"{FormatCount(_renderer.DrawnPointCount)} drawn";
            if (_statDraws != null)  _statDraws.text  = $"{_renderer.DrawCallCount} draw{(_renderer.DrawCallCount == 1 ? "" : "s")}";
            if (_statVram != null)   _statVram.text   = $"{_renderer.VramBytes / (1024 * 1024)} MB";
            if (_statFrame != null)  _statFrame.text  = $"{_smoothedFrameMs:F1} ms";
        }

        /// <summary>
        /// Whether a screen point is over actual panel chrome.
        ///
        /// EventSystem.IsPointerOverGameObject() does not work for UI Toolkit, so the panel
        /// is queried directly. Callers must latch this on pointer-down and hold it for the
        /// whole drag; testing it per frame makes a drag that starts in the viewport stutter
        /// the moment the cursor crosses a panel.
        /// </summary>
        public bool IsPointerOverUi(Vector2 screenPosition)
        {
            var panel = _root?.panel;
            if (panel == null) return false;

            var panelPosition = RuntimePanelUtils.ScreenToPanel(
                panel, new Vector2(screenPosition.x, Screen.height - screenPosition.y));

            var picked = panel.Pick(panelPosition);
            return picked != null && picked != _root;
        }

        static string FormatCount(int n) =>
            n >= 1_000_000 ? $"{n / 1_000_000f:0.##}M" : n >= 1000 ? $"{n / 1000f:0.#}K" : n.ToString();

        /// <summary>"RadialDistance" becomes "Radial distance".</summary>
        static string Humanize(string pascalCase)
        {
            if (string.IsNullOrEmpty(pascalCase)) return pascalCase;

            var builder = new System.Text.StringBuilder(pascalCase.Length + 4);
            builder.Append(pascalCase[0]);

            for (int i = 1; i < pascalCase.Length; i++)
            {
                if (char.IsUpper(pascalCase[i]) && !char.IsUpper(pascalCase[i - 1]))
                {
                    builder.Append(' ');
                    builder.Append(char.ToLowerInvariant(pascalCase[i]));
                }
                else builder.Append(pascalCase[i]);
            }
            return builder.ToString();
        }

        public void Dispose()
        {
            _cloudItems.Clear();
            SelectedCloud = null;
        }
    }
}
