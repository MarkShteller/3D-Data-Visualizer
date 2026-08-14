using System;
using System.Collections.Generic;
using PointCloud.Core.Diagnostics;
using UnityEngine;

namespace PointCloud.App.Bootstrap
{
    /// <summary>
    /// The single owner of every long-lived object in the application.
    ///
    /// Deliberately an instance rather than a set of statics: this project has both
    /// domain reload and scene reload disabled (fast play mode), so a static that owns
    /// a NativeArray or a GraphicsBuffer leaks it into the *next* play session. Every
    /// disposable in the app is registered here and released in Dispose().
    /// </summary>
    public sealed class AppServices : IDisposable
    {
        readonly List<IDisposable> _owned = new(32);
        bool _disposed;

        public LoadLog Log { get; }

        public AppServices()
        {
            Log = new LoadLog();
        }

        /// <summary>
        /// Hand ownership of a disposable to the app. Returns the same instance so it can
        /// be used inline: <c>var x = services.Own(new Thing());</c>
        /// </summary>
        public T Own<T>(T disposable) where T : IDisposable
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AppServices),
                    "Cannot register a disposable after AppServices has been disposed.");
            _owned.Add(disposable);
            return disposable;
        }

        /// <summary>
        /// Release and forget a disposable early (e.g. the user closed one cloud).
        /// Safe to call with something that was never registered.
        /// </summary>
        public void Release(IDisposable disposable)
        {
            if (disposable == null) return;
            _owned.Remove(disposable);
            TryDispose(disposable);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Reverse order: later registrations may depend on earlier ones.
            for (int i = _owned.Count - 1; i >= 0; i--)
                TryDispose(_owned[i]);
            _owned.Clear();
        }

        static void TryDispose(IDisposable d)
        {
            try
            {
                d?.Dispose();
            }
            catch (Exception e)
            {
                // A throwing Dispose must not strand the remaining resources.
                Debug.LogError($"AppServices: {d?.GetType().Name} threw during Dispose: {e}");
            }
        }
    }
}
