using System;
using System.Threading;
using PointCloud.Core.Data;

namespace PointCloud.Core.Sources
{
    /// <summary>
    /// One decoded frame of point data.
    ///
    /// Reference counted because a sequence has three independent holders — the prefetch
    /// ring, the renderer showing the current frame, and the inspector reading a picked
    /// point — and any of them releasing must not free NativeArrays out from under the
    /// others. A static frame is just a sequence of length one, so it uses the same type
    /// rather than a parallel path.
    /// </summary>
    public sealed class PointCloudFrame : IDisposable
    {
        int _refCount = 1;
        PointCloudData _data;

        public int    FrameIndex  { get; }
        public double TimeSeconds { get; }

        public PointCloudData Data =>
            _data ?? throw new ObjectDisposedException(nameof(PointCloudFrame),
                "This frame's data has already been released.");

        public bool IsAlive => _data != null;

        public PointCloudFrame(PointCloudData data, int frameIndex = 0, double timeSeconds = 0.0)
        {
            _data       = data ?? throw new ArgumentNullException(nameof(data));
            FrameIndex  = frameIndex;
            TimeSeconds = timeSeconds;
        }

        /// <summary>Take a reference. Every Retain needs a matching Dispose.</summary>
        public PointCloudFrame Retain()
        {
            int count = Interlocked.Increment(ref _refCount);
            if (count <= 1)
                throw new ObjectDisposedException(nameof(PointCloudFrame),
                    "Cannot retain a frame that has already been released.");
            return this;
        }

        /// <summary>Release a reference. The underlying data is freed at zero.</summary>
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _refCount) > 0) return;

            var data = Interlocked.Exchange(ref _data, null);
            data?.Dispose();
        }
    }
}
