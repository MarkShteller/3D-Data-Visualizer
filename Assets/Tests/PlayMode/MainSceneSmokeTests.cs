using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using PointCloud.App.Bootstrap;
using PointCloud.App.UI;
using PointCloud.App.Viewer;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PointCloud.Tests.PlayMode
{
    /// <summary>
    /// Loads Main.unity exactly as pressing Play does, and checks the whole app actually
    /// comes up.
    ///
    /// Every other render test builds its objects by hand, which means none of them touch
    /// the scene, the bootstrapper, the camera rig, or the UI document — the parts most
    /// likely to be broken by a missing reference rather than by bad logic. This is the
    /// test that answers "can I press Play?".
    ///
    /// Note: no WaitForEndOfFrame anywhere. It never resumes under -batchmode and hangs the
    /// run instead of failing it. Plain `yield return null` advances a frame fine.
    /// </summary>
    public class MainSceneSmokeTests
    {
        const string ScenePath = "Assets/App/Scenes/Main.unity";

        readonly List<string> _errors = new();

        [UnitySetUp]
        public IEnumerator LoadMainScene()
        {
            _errors.Clear();
            Application.logMessageReceived += CaptureError;

            var load = SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Single);
            Assert.IsNotNull(load, $"{ScenePath} is not in the build settings.");

            while (!load.isDone) yield return null;

            // A few frames so Awake, Start and the first LateUpdate have all run.
            for (int i = 0; i < 5; i++) yield return null;
        }

        [TearDown]
        public void TearDown() => Application.logMessageReceived -= CaptureError;

        void CaptureError(string condition, string stackTrace, LogType type)
        {
            if (type is LogType.Error or LogType.Exception or LogType.Assert)
                _errors.Add($"{type}: {condition}");
        }

        static T FindOne<T>() where T : Object => Object.FindAnyObjectByType<T>();

        [UnityTest]
        public IEnumerator SceneComesUpWithoutErrors()
        {
            Assert.IsEmpty(_errors, "Errors were logged during startup:\n" + string.Join("\n", _errors));

            var bootstrap = FindOne<AppBootstrap>();
            Assert.IsNotNull(bootstrap, "No AppBootstrap in the scene.");
            Assert.IsNotNull(bootstrap.Services, "AppServices was never constructed.");
            Assert.Greater(bootstrap.Services.Log.Count, 0, "The startup banner was not logged.");

            Assert.IsNotNull(Camera.main, "No camera tagged MainCamera.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator ViewerLoadsAndDrawsTheStartupCloud()
        {
            var viewer = FindOne<PointCloudViewer>();
            Assert.IsNotNull(viewer, "No PointCloudViewer in the scene.");
            Assert.IsNotNull(viewer.Renderer, "The viewer never created a renderer.");

            Assert.AreEqual(1, viewer.Renderer.Clouds.Count,
                "Expected exactly one startup cloud.");

            var cloud = viewer.Renderer.Clouds[0];
            Assert.Greater(cloud.Descriptor.PointCount, 0);

            // Upload is sliced across frames, so give the pump time to drain.
            for (int i = 0; i < 120 && !cloud.IsFullyUploaded; i++) yield return null;

            Assert.IsTrue(cloud.IsFullyUploaded,
                $"Only {cloud.UploadedPointCount:N0} of {cloud.Descriptor.PointCount:N0} points " +
                "became resident within 120 frames.");

            yield return null;

            Assert.Greater(viewer.Renderer.DrawCallCount, 0, "Nothing was submitted for drawing.");
            Assert.AreEqual(cloud.Descriptor.PointCount, viewer.Renderer.DrawnPointCount,
                "The draw did not cover every point.");

            TestContext.WriteLine(
                $"Startup cloud: {cloud.Descriptor} · {cloud.VramBytes / (1024 * 1024)} MB VRAM · " +
                $"mode {cloud.Display.ColorMode} · {viewer.Renderer.DrawCallCount} draw call(s)");
        }

        [UnityTest]
        public IEnumerator CameraFramesTheCloudAndFitsClipPlanes()
        {
            var viewer = FindOne<PointCloudViewer>();
            var cloud = viewer.Renderer.Clouds[0];
            var camera = Camera.main;

            yield return null;

            // Framing should put the camera outside the cloud but looking at it.
            float distance = Vector3.Distance(camera.transform.position, cloud.WorldBounds.center);
            Assert.Greater(distance, cloud.WorldBounds.extents.magnitude * 0.5f,
                "The camera was framed inside the cloud.");

            // ClipPlaneFitter must have replaced the scene defaults with a fitted range.
            Assert.Less(camera.farClipPlane / camera.nearClipPlane, 1e4f * 1.01f,
                $"far/near is {camera.farClipPlane / camera.nearClipPlane:F0}; ClipPlaneFitter caps it at 1e4 " +
                "so EDL's log-depth term stays stable.");
            Assert.Greater(camera.nearClipPlane, 0f);
            Assert.Less(camera.nearClipPlane, camera.farClipPlane);

            TestContext.WriteLine(
                $"Camera at {camera.transform.position}, distance {distance:F1}, " +
                $"near {camera.nearClipPlane:F4}, far {camera.farClipPlane:F1}, " +
                $"ratio {camera.farClipPlane / camera.nearClipPlane:F0}");
        }

        [UnityTest]
        public IEnumerator UiDocumentBuildsItsPanels()
        {
            var document = FindOne<UIDocument>();
            Assert.IsNotNull(document, "No UIDocument in the scene.");
            Assert.IsNotNull(document.panelSettings,
                "UIDocument has no PanelSettings — nothing would render.");
            Assert.IsNotNull(document.panelSettings.themeStyleSheet,
                "PanelSettings has no theme style sheet — controls look fine in the editor " +
                "and are invisible in a build.");
            Assert.IsNotNull(document.visualTreeAsset, "UIDocument has no UXML.");

            yield return null;

            var root = document.rootVisualElement;
            Assert.IsNotNull(root, "The document produced no root visual element.");

            foreach (var name in new[] { "stats-hud", "dock-left", "dock-right", "cloud-list",
                                         "color-mode", "colormap", "pixel-size", "attribute-list" })
                Assert.IsNotNull(root.Q(name), $"UXML element '{name}' is missing from the built panel.");

            // The mode dropdown must be populated from the loaded cloud, not left empty.
            var colorMode = root.Q<DropdownField>("color-mode");
            Assert.Greater(colorMode.choices.Count, 0, "The render mode dropdown is empty.");

            // The cloud list must show the startup cloud.
            var cloudList = root.Q<ListView>("cloud-list");
            Assert.AreEqual(1, cloudList.itemsSource?.Count ?? 0,
                "The cloud list does not show the loaded cloud.");

            TestContext.WriteLine(
                $"UI built: {colorMode.choices.Count} render modes, " +
                $"{root.Q<DropdownField>("colormap").choices.Count} colormaps, " +
                $"cloud list shows {cloudList.itemsSource.Count} cloud(s)");
        }

        /// <summary>
        /// The theme must actually resolve.
        ///
        /// Checking that elements exist proves nothing about styling: if a USS custom
        /// property fails to resolve, var() silently falls back and panels render
        /// transparent or in the default theme's light chrome, while every structural
        /// assertion still passes.
        /// </summary>
        [UnityTest]
        public IEnumerator ThemeAppliesThePalette()
        {
            var document = FindOne<UIDocument>();
            var camera = Camera.main;
            yield return null;

            // The viewport background is the darkest palette entry.
            AssertColor(UiPalette.SceneBackground, camera.backgroundColor, "camera background");

            var dock = document.rootVisualElement.Q("dock-left");
            Assert.IsNotNull(dock);

            var surface = dock.resolvedStyle.backgroundColor;
            Assert.Greater(surface.a, 0.5f,
                $"dock-left resolved to a near-transparent background ({surface}); " +
                "the USS custom properties are not resolving.");
            AssertColor(UiPalette.PanelSurface, surface, "panel surface", alphaTolerant: true);

            // Panels must separate from the viewport behind them, which the palette has no
            // mid-tone neutral for — hence the lift toward Purple.
            Assert.Greater(Luminance(surface), Luminance(UiPalette.SceneBackground) * 1.2f,
                "The panel surface is not lighter than the scene background, so panels will " +
                "have no edge against the viewport.");

            // Body text must be one of the two high-contrast entries, never a purple.
            var name = document.rootVisualElement.Q<Label>("stat-points");
            var textColor = name.resolvedStyle.color;
            float ratio = ContrastRatio(textColor, surface);
            Assert.Greater(ratio, 4.5f,
                $"HUD text contrast is {ratio:F1}:1 against the panel, below the 4.5:1 threshold.");

            TestContext.WriteLine(
                $"scene {ToHex(camera.backgroundColor)} · panel {ToHex(surface)} · " +
                $"HUD text {ToHex(textColor)} at {ratio:F1}:1");
        }

        static void AssertColor(Color expected, Color actual, string what, bool alphaTolerant = false)
        {
            const float tolerance = 0.02f;
            Assert.AreEqual(expected.r, actual.r, tolerance, $"{what} red channel");
            Assert.AreEqual(expected.g, actual.g, tolerance, $"{what} green channel");
            Assert.AreEqual(expected.b, actual.b, tolerance, $"{what} blue channel");
            if (!alphaTolerant) Assert.AreEqual(expected.a, actual.a, tolerance, $"{what} alpha");
        }

        /// <summary>WCAG relative luminance.</summary>
        static float Luminance(Color c)
        {
            static float Channel(float v) => v <= 0.03928f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
            return 0.2126f * Channel(c.r) + 0.7152f * Channel(c.g) + 0.0722f * Channel(c.b);
        }

        static float ContrastRatio(Color a, Color b)
        {
            float la = Luminance(a), lb = Luminance(b);
            return (Mathf.Max(la, lb) + 0.05f) / (Mathf.Min(la, lb) + 0.05f);
        }

        static string ToHex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        /// <summary>
        /// The root panel must not swallow viewport drags. If picking-mode Ignore ever comes
        /// off the root element, the camera stops responding everywhere and the cause is far
        /// from obvious.
        /// </summary>
        [UnityTest]
        public IEnumerator ViewportCentreIsNotCapturedByTheUi()
        {
            var document = FindOne<UIDocument>();
            yield return null;

            var panel = document.rootVisualElement.panel;
            var centre = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
            var picked = panel.Pick(centre);

            Assert.IsTrue(picked == null || picked == document.rootVisualElement,
                $"The centre of the viewport is covered by UI element '{picked?.name}' " +
                "('{picked?.GetType().Name}'), so camera drags there would be ignored.");
        }
    }
}
