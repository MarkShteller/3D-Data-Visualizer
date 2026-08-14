using System.IO;
using PointCloud.App.Bootstrap;
using PointCloud.App.UI;
using PointCloud.App.Viewer;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace PointCloud.Editor
{
    /// <summary>
    /// Builds Assets/App/Scenes/Main.unity and its PanelSettings from scratch.
    ///
    /// Generated rather than checked in: a hand-authored scene is a wall of YAML with GUID
    /// references that nobody can review, and this scene is simple enough that the code
    /// below is the readable description of it.
    /// </summary>
    public static class MainSceneBuilder
    {
        const string ScenePath         = "Assets/App/Scenes/Main.unity";
        const string PanelSettingsPath = "Assets/App/UI/Settings/PointCloudPanelSettings.asset";
        const string ThemePath         = "Assets/App/UI/Settings/PointCloudTheme.tss";
        const string RootUxmlPath      = "Assets/App/UI/Uxml/Root.uxml";

        [MenuItem("Tools/Point Cloud/Create or Rebuild Main Scene")]
        public static void BuildMainScene()
        {
            // EditorUtility.DisplayDialog always answers "cancel" under -batchmode, so the
            // confirmation is interactive-only — otherwise CI could never rebuild the scene.
            if (!Application.isBatchMode)
            {
                if (File.Exists(ScenePath) &&
                    !EditorUtility.DisplayDialog("Rebuild Main Scene?",
                        $"{ScenePath} already exists and will be overwritten.\n\n" +
                        "Any manual edits to it will be lost.", "Rebuild", "Cancel"))
                    return;

                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath)!);

            CreateOrUpdatePanelSettings();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Load the assets AFTER creating the scene, not before. NewScene unloads assets
            // nothing references yet, which destroys a just-created PanelSettings out from
            // under a C# reference — and assigning a destroyed UnityEngine.Object silently
            // serialises as null rather than throwing.
            var panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            var rootUxml      = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(RootUxmlPath);

            if (panelSettings == null)
                Debug.LogError($"[PointCloud Setup] {PanelSettingsPath} could not be loaded — the UI will not render.");
            if (rootUxml == null)
                Debug.LogWarning($"[PointCloud Setup] {RootUxmlPath} not found — the UI will be empty.");

            // --- Camera -------------------------------------------------------------
            // Near/far are placeholders: ClipPlaneFitter refits them every frame from the
            // visible bounds, which is what keeps EDL's log-depth term stable later.
            var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags       = CameraClearFlags.SolidColor;
            // Driven from UiPalette so the viewport and the panels cannot drift apart.
            camera.backgroundColor  = UiPalette.SceneBackground;
            camera.nearClipPlane    = 0.05f;
            camera.farClipPlane     = 1000f;
            camera.fieldOfView      = 55f;
            cameraGo.transform.SetPositionAndRotation(new Vector3(0f, 4f, -18f), Quaternion.Euler(10f, 0f, 0f));

            // --- UI -----------------------------------------------------------------
            var uiGo = new GameObject("UI");
            var uiDocument = uiGo.AddComponent<UIDocument>();
            uiDocument.panelSettings = panelSettings;
            uiDocument.visualTreeAsset = rootUxml;

            // Runtime UI Toolkit routes pointer and key events through an EventSystem with
            // the Input System module; without one, no control ever receives a click.
            var eventSystemGo = new GameObject("EventSystem");
            eventSystemGo.AddComponent<UnityEngine.EventSystems.EventSystem>();
            eventSystemGo.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();

            // --- App ----------------------------------------------------------------
            var appGo = new GameObject("App");
            appGo.AddComponent<AppBootstrap>();

            var viewer = appGo.AddComponent<PointCloudViewer>();
            var serialized = new SerializedObject(viewer);
            serialized.FindProperty("_camera").objectReferenceValue = camera;
            serialized.FindProperty("_uiDocument").objectReferenceValue = uiDocument;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // No lights: every point cloud mode is unlit, and an unused directional light in
            // a Forward+ scene still costs a shadow-map pass.

            // Verify before saving. A UIDocument with no PanelSettings renders nothing at
            // all, and silently — worth failing loudly here rather than discovering it as
            // an empty screen later.
            if (uiDocument.panelSettings == null)
                Debug.LogError("[PointCloud Setup] UIDocument.panelSettings did not persist — the UI will not render.");
            if (uiDocument.visualTreeAsset == null)
                Debug.LogError("[PointCloud Setup] UIDocument.visualTreeAsset did not persist — the UI will be empty.");

            EditorSceneManager.SaveScene(scene, ScenePath);
            SetAsOnlyBuildScene();

            Debug.Log($"[PointCloud Setup] Created {ScenePath} and set it as the only build scene " +
                      $"(panel={uiDocument.panelSettings != null}, uxml={uiDocument.visualTreeAsset != null}).");
        }

        /// <summary>
        /// A PanelSettings asset whose Theme Style Sheet is unset renders every control
        /// invisible in a build while looking perfectly fine in the editor. Wiring it here
        /// means that failure mode simply cannot occur.
        /// </summary>
        static void CreateOrUpdatePanelSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PanelSettingsPath)!);

            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            bool created = settings == null;
            if (created) settings = ScriptableObject.CreateInstance<PanelSettings>();

            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (theme != null) settings.themeStyleSheet = theme;
            else Debug.LogWarning($"[PointCloud Setup] {ThemePath} not found — runtime controls will be invisible in a build.");

            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(1920, 1080);
            settings.match = 0.5f;   // balance width and height so a wide window keeps text size
            settings.clearColor = false;

            if (created) AssetDatabase.CreateAsset(settings, PanelSettingsPath);
            else EditorUtility.SetDirty(settings);

            AssetDatabase.SaveAssets();
        }

        static void SetAsOnlyBuildScene()
        {
            // The path constructor resolves the GUID itself; SampleScene drops out because
            // this replaces the list rather than appending to it.
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
