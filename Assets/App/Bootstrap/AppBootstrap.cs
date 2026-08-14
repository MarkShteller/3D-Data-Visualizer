using System;
using PointCloud.Core.Diagnostics;
using UnityEngine;

namespace PointCloud.App.Bootstrap
{
    /// <summary>
    /// Entry point. Lives on a single GameObject in Main.unity, constructs AppServices,
    /// and guarantees it is disposed exactly once on shutdown.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class AppBootstrap : MonoBehaviour
    {
        public const string Version = "0.1.0";

        public AppServices Services { get; private set; }

        void Awake()
        {
            Services = new AppServices();
            StaticReset.Current = Services;

            Application.quitting += OnApplicationQuitting;

            Services.Log.Info("App",
                $"3D Data Visualizer {Version} — Unity {Application.unityVersion}, " +
                $"{SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName} " +
                $"({SystemInfo.graphicsMemorySize} MB VRAM)");
            Services.Log.Info("App",
                $"maxGraphicsBufferSize = {SystemInfo.maxGraphicsBufferSize / (1024L * 1024L)} MB, " +
                $"supportsComputeShaders = {SystemInfo.supportsComputeShaders}, " +
                $"processorCount = {SystemInfo.processorCount}");

            // Mirror the banner to the Unity console so it shows up in player logs too.
            Debug.Log(Services.Log.Snapshot()[0].Message);
        }

        void OnDestroy()   => Shutdown();

        void OnApplicationQuitting() => Shutdown();

        void Shutdown()
        {
            if (Services == null) return;

            Application.quitting -= OnApplicationQuitting;

            var services = Services;
            Services = null;
            if (ReferenceEquals(StaticReset.Current, services))
                StaticReset.Current = null;

            try
            {
                services.Dispose();
            }
            catch (Exception e)
            {
                Debug.LogError($"AppBootstrap: shutdown failed: {e}");
            }
        }
    }
}
