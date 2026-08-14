using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using PointCloud.App.Viewer;
using PointCloud.Core.Data;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PointCloud.Tests.PlayMode
{
    /// <summary>
    /// Opens a real file the way the UI does, in the real scene.
    ///
    /// This is the path a user actually takes and the one nothing else covers: the render
    /// tests build their objects by hand, and the EditMode tests stop at decoded data. Here
    /// the scene's own viewer, services, registry and loader do the work.
    /// </summary>
    public class ViewerFileLoadTests
    {
        const string ScenePath = "Assets/App/Scenes/Main.unity";
        const int MaxFrames = 900;   // generous: a load runs across frames

        string _path;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            var directory = Path.Combine(Application.dataPath, "Resources");
            _path = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.ply", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;

            if (_path == null)
                Assert.Ignore("No .ply in Assets/Resources — this validation needs a real sample file.");

            var load = SceneManager.LoadSceneAsync(ScenePath, LoadSceneMode.Single);
            while (!load.isDone) yield return null;
            for (int i = 0; i < 5; i++) yield return null;
        }

        static PointCloudViewer Viewer() => Object.FindAnyObjectByType<PointCloudViewer>();

        [UnityTest]
        public IEnumerator OpeningAFile_AddsItAlongsideTheExistingCloud()
        {
            var viewer = Viewer();
            Assert.IsNotNull(viewer);

            int before = viewer.Renderer.Clouds.Count;
            Assert.Greater(before, 0, "Expected the startup synthetic cloud to be present.");

            // Fire and poll rather than await: the load resumes on the main thread, so
            // blocking on it here would dead-lock the very thread it needs.
            _ = viewer.OpenFilesAsync(new[] { _path });

            int frames = 0;
            while (viewer.IsLoading && frames++ < MaxFrames) yield return null;
            Assert.IsFalse(viewer.IsLoading, $"Load did not finish within {MaxFrames} frames.");

            Assert.AreEqual(before + 1, viewer.Renderer.Clouds.Count,
                "Opening a file must ADD a cloud, not replace what is loaded — overlaying a " +
                "prediction on its ground truth is the comparison this tool exists for.");

            var loaded = viewer.Renderer.Clouds.Last();
            Assert.AreEqual("ply", loaded.Descriptor.FormatId);
            Assert.Greater(loaded.Descriptor.PointCount, 0);
            Assert.IsTrue(loaded.Descriptor.Has(PointAttributes.Position));

            // Let the sliced upload pump drain, then confirm it actually draws.
            frames = 0;
            while (!loaded.IsFullyUploaded && frames++ < MaxFrames) yield return null;
            Assert.IsTrue(loaded.IsFullyUploaded,
                $"Only {loaded.UploadedPointCount:N0} of {loaded.Descriptor.PointCount:N0} points uploaded.");

            yield return null;

            Assert.Greater(viewer.Renderer.DrawCallCount, 0, "Nothing was drawn after loading.");
            Assert.AreEqual(2, viewer.Renderer.DrawCallCount,
                "Two clouds should be two indirect draws.");

            TestContext.WriteLine(
                $"{Path.GetFileName(_path)} → {loaded.Descriptor.PointCount:N0} points, " +
                $"{loaded.VramBytes / (1024 * 1024)} MB VRAM, mode {loaded.Display.ColorMode}, " +
                $"{viewer.Renderer.DrawCallCount} draw calls total");
        }

        [UnityTest]
        public IEnumerator OpenControlsAreWiredIntoTheUi()
        {
            var document = Object.FindAnyObjectByType<UIDocument>();
            Assert.IsNotNull(document);
            yield return null;

            var root = document.rootVisualElement;
            foreach (var name in new[] { "open-file", "clear-clouds", "cancel-load",
                                         "recent-files", "path-field", "load-status", "load-progress" })
                Assert.IsNotNull(root.Q(name), $"Open control '{name}' is missing from the panel.");

            // The progress controls must be hidden until something is actually loading, or
            // the panel shows a permanently empty bar.
            Assert.AreEqual(DisplayStyle.None, root.Q<ProgressBar>("load-progress").resolvedStyle.display,
                "The progress bar should be hidden when idle.");
            Assert.IsTrue(root.Q<Button>("open-file").enabledSelf);
        }

        [UnityTest]
        public IEnumerator ClearRemovesEveryCloudAndReleasesVram()
        {
            var viewer = Viewer();

            _ = viewer.OpenFilesAsync(new[] { _path });
            int frames = 0;
            while (viewer.IsLoading && frames++ < MaxFrames) yield return null;

            Assert.Greater(viewer.Renderer.Clouds.Count, 0);

            viewer.ClearClouds();
            yield return null;

            Assert.AreEqual(0, viewer.Renderer.Clouds.Count);
            Assert.AreEqual(0, viewer.Renderer.DrawCallCount);
            Assert.AreEqual(0, viewer.Renderer.VramBytes, "VRAM was not released on clear.");
        }

        [UnityTest]
        public IEnumerator OpeningAMissingFile_ReportsWithoutThrowing()
        {
            var viewer = Viewer();
            int before = viewer.Renderer.Clouds.Count;

            _ = viewer.OpenFilesAsync(new[] { Path.Combine(Application.dataPath, "nope_does_not_exist.ply") });

            int frames = 0;
            while (viewer.IsLoading && frames++ < MaxFrames) yield return null;

            Assert.AreEqual(before, viewer.Renderer.Clouds.Count,
                "A failed load must not add a cloud.");

            var status = Object.FindAnyObjectByType<UIDocument>().rootVisualElement.Q<Label>("load-status");
            Assert.IsNotEmpty(status.text, "A failed load must say something to the user.");
            TestContext.WriteLine($"status: {status.text}");
        }
    }
}
