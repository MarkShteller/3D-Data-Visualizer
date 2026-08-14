using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace PointCloud.Editor
{
    /// <summary>
    /// Applies the project-level configuration this tool depends on.
    ///
    /// These live in ProjectSettings/*.asset and Assets/Settings/*.asset, which the open
    /// editor holds in memory — hand-editing the YAML on disk gets silently clobbered on
    /// the next save. Going through the real APIs is the only reliable path, so setup is
    /// a menu item rather than a checked-in file diff.
    ///
    /// Idempotent: running it twice is a no-op and reports "already correct".
    /// </summary>
    public static class ProjectSetupValidator
    {
        const string MenuRoot = "Tools/Point Cloud/";

        [MenuItem(MenuRoot + "Apply Project Setup")]
        public static void ApplySetup()
        {
            var changes = new List<string>();

            ApplyPlayerSettings(changes);
            ApplyRenderPipelineSettings(changes);

            if (changes.Count == 0)
            {
                Debug.Log("[PointCloud Setup] Everything already correct — no changes made.");
                return;
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[PointCloud Setup] Applied:\n  - " + string.Join("\n  - ", changes) +
                      "\n\nProjectSettings changes are written when the editor next saves " +
                      "(File > Save Project forces it now).");
        }

        [MenuItem(MenuRoot + "Validate Project Setup")]
        public static void ValidateSetup()
        {
            var problems = new List<string>();

            if (!PlayerSettings.runInBackground)
                problems.Add("runInBackground is off — the app will freeze mid-load on alt-tab.");
            if (!PlayerSettings.resizableWindow)
                problems.Add("resizableWindow is off.");
            if (PlayerSettings.fullScreenMode != FullScreenMode.Windowed)
                problems.Add($"fullScreenMode is {PlayerSettings.fullScreenMode}, expected Windowed.");
            if (!PlayerSettings.enableFrameTimingStats)
                problems.Add("enableFrameTimingStats is off — the stats HUD cannot report GPU ms.");
            if (PlayerSettings.colorSpace != ColorSpace.Linear)
                problems.Add($"colorSpace is {PlayerSettings.colorSpace}, expected Linear.");

            foreach (var (asset, label) in EnumerateRenderPipelineAssets())
            {
                if (!asset.supportsCameraDepthTexture)
                    problems.Add($"{label}: Depth Texture is off — EDL needs it.");
                if (asset.supportsCameraOpaqueTexture)
                    problems.Add($"{label}: Opaque Texture is on — a full-res copy every frame that nothing samples.");

                var renderer = GetRendererData(asset);
                if (renderer == null) continue;

                if (renderer.renderingMode != RenderingMode.ForwardPlus)
                    problems.Add($"{label} renderer: rendering mode is {renderer.renderingMode}, expected ForwardPlus.");

                foreach (var f in renderer.rendererFeatures.Where(f => f is ScreenSpaceAmbientOcclusion && f.isActive))
                    problems.Add($"{label} renderer: '{f.name}' is active — it would double up with EDL.");
            }

            if (problems.Count == 0)
                Debug.Log("[PointCloud Setup] Project setup is valid.");
            else
                Debug.LogWarning("[PointCloud Setup] Problems found:\n  - " + string.Join("\n  - ", problems) +
                                 $"\n\nRun {MenuRoot}Apply Project Setup to fix.");
        }

        static void ApplyPlayerSettings(List<string> changes)
        {
            // A multi-minute load must survive an alt-tab.
            Set(ref changes, "runInBackground = true",
                !PlayerSettings.runInBackground, () => PlayerSettings.runInBackground = true);

            Set(ref changes, "resizableWindow = true",
                !PlayerSettings.resizableWindow, () => PlayerSettings.resizableWindow = true);

            Set(ref changes, "fullScreenMode = Windowed",
                PlayerSettings.fullScreenMode != FullScreenMode.Windowed,
                () => PlayerSettings.fullScreenMode = FullScreenMode.Windowed);

            Set(ref changes, "default resolution = 1600x900",
                PlayerSettings.defaultScreenWidth != 1600 || PlayerSettings.defaultScreenHeight != 900,
                () => { PlayerSettings.defaultScreenWidth = 1600; PlayerSettings.defaultScreenHeight = 900; });

            // Required for FrameTimingManager.GetLatestTimings() to report GPU ms in the HUD.
            Set(ref changes, "enableFrameTimingStats = true",
                !PlayerSettings.enableFrameTimingStats, () => PlayerSettings.enableFrameTimingStats = true);

            Set(ref changes, "companyName / productName",
                PlayerSettings.companyName == "DefaultCompany",
                () => PlayerSettings.companyName = "Diamond Dust Games");

            const string bundleId = "com.diamonddustgames.pointcloudviz";
            var nbt = NamedBuildTarget.Standalone;
            Set(ref changes, $"Standalone application identifier = {bundleId}",
                PlayerSettings.GetApplicationIdentifier(nbt) != bundleId,
                () => PlayerSettings.SetApplicationIdentifier(nbt, bundleId));
        }

        static void ApplyRenderPipelineSettings(List<string> changes)
        {
            foreach (var (asset, label) in EnumerateRenderPipelineAssets())
            {
                // EDL samples _CameraDepthTexture. The opaque colour copy, by contrast, is a
                // full-res blit every frame that nothing in this app ever reads.
                Set(ref changes, $"{label}: Depth Texture on",
                    !asset.supportsCameraDepthTexture,
                    () => { asset.supportsCameraDepthTexture = true; EditorUtility.SetDirty(asset); });

                Set(ref changes, $"{label}: Opaque Texture off",
                    asset.supportsCameraOpaqueTexture,
                    () => { asset.supportsCameraOpaqueTexture = false; EditorUtility.SetDirty(asset); });

                var renderer = GetRendererData(asset);
                if (renderer == null)
                {
                    Debug.LogWarning($"[PointCloud Setup] {label} has no UniversalRendererData — skipped.");
                    continue;
                }

                // NOTE: m_RenderingMode: 2 is ForwardPlus, not Deferred (Deferred = 1).
                // This project ships on Forward+ already; the check is here to keep it that way.
                Set(ref changes, $"{label} renderer: rendering mode = ForwardPlus",
                    renderer.renderingMode != RenderingMode.ForwardPlus,
                    () => { renderer.renderingMode = RenderingMode.ForwardPlus; EditorUtility.SetDirty(renderer); });

                // Points are unlit data visualisation and EDL *is* the ambient occlusion for
                // them. Leave the feature in the asset (for future lit context geometry),
                // just inactive.
                foreach (var feature in renderer.rendererFeatures)
                {
                    if (feature is not ScreenSpaceAmbientOcclusion || !feature.isActive) continue;
                    var f = feature;
                    Set(ref changes, $"{label} renderer: disabled '{f.name}'",
                        true,
                        () => { f.SetActive(false); EditorUtility.SetDirty(renderer); });
                }
            }
        }

        /// <summary>Every URP asset referenced by a quality level, plus the global default.</summary>
        static IEnumerable<(UniversalRenderPipelineAsset asset, string label)> EnumerateRenderPipelineAssets()
        {
            var seen = new HashSet<UniversalRenderPipelineAsset>();

            if (GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset def && seen.Add(def))
                yield return (def, def.name);

            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                if (QualitySettings.GetRenderPipelineAssetAt(i) is UniversalRenderPipelineAsset a && seen.Add(a))
                    yield return (a, a.name);
            }
        }

        /// <summary>
        /// UniversalRenderPipelineAsset exposes no public accessor for its renderer data
        /// list, so reach it through the serialised property. Stable across URP 14-17.
        /// </summary>
        static UniversalRendererData GetRendererData(UniversalRenderPipelineAsset asset)
        {
            using var so = new SerializedObject(asset);
            var list = so.FindProperty("m_RendererDataList");
            if (list == null || list.arraySize == 0) return null;

            var index = so.FindProperty("m_DefaultRendererIndex");
            int i = index != null ? Mathf.Clamp(index.intValue, 0, list.arraySize - 1) : 0;
            return list.GetArrayElementAtIndex(i).objectReferenceValue as UniversalRendererData;
        }

        static void Set(ref List<string> changes, string description, bool needed, System.Action apply)
        {
            if (!needed) return;
            apply();
            changes.Add(description);
        }
    }
}
