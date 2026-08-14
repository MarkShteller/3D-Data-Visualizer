using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PointCloud.Core.Data;

namespace PointCloud.Core.Sources
{
    /// <summary>What the caller wants from a frame. Lets a parser skip work it does not need.</summary>
    public readonly struct FrameRequest
    {
        /// <summary>
        /// Attributes to decode. A parser may skip source properties outside this mask
        /// entirely, which on a wide PLY is most of the file.
        /// </summary>
        public readonly PointAttributes Wanted;

        /// <summary>When positive, subsample to at most this many points while parsing.</summary>
        public readonly int MaxPoints;

        public readonly CpuRetention Retention;

        /// <summary>0 uses ChunkBuilder's default.</summary>
        public readonly int ChunkSize;

        public FrameRequest(PointAttributes wanted = AllAttributes, int maxPoints = 0,
                            CpuRetention retention = CpuRetention.PositionsOnly, int chunkSize = 0)
        {
            Wanted    = wanted;
            MaxPoints = maxPoints;
            Retention = retention;
            ChunkSize = chunkSize;
        }

        const PointAttributes AllAttributes = (PointAttributes)0xFFFFFFFF;

        /// <summary>Everything the file has, positions kept on the CPU for picking.</summary>
        public static FrameRequest Default => new(AllAttributes);

        public bool Wants(PointAttributes attribute) => (Wanted & attribute) != 0;
    }

    /// <summary>
    /// A source of point cloud data.
    ///
    /// Frame-sequenced from the start even though every phase-1 format is a single static
    /// frame. That is deliberate: it is the seam a VRS stream drops into without the
    /// renderer, the transport or the UI changing shape. A static file simply reports
    /// FrameCount == 1.
    /// </summary>
    public interface IPointCloudSource : IAsyncDisposable
    {
        /// <summary>Format id: "ply", "pcd", "obj", "fbx", "vrs", "synthetic".</summary>
        string Id { get; }

        string DisplayName { get; }

        SourceCapabilities Capabilities { get; }

        /// <summary>Valid only after <see cref="OpenAsync"/> completes.</summary>
        PointCloudDescriptor Metadata { get; }

        /// <summary>1 for a static file.</summary>
        int FrameCount { get; }

        double DurationSeconds { get; }

        /// <summary>Read headers and populate <see cref="Metadata"/>. Cheap; does not decode points.</summary>
        Task OpenAsync(IProgress<LoadProgress> progress, CancellationToken cancellationToken);

        /// <summary>Decode one frame. The caller owns the returned frame and must dispose it.</summary>
        Task<PointCloudFrame> ReadFrameAsync(int frameIndex, FrameRequest request,
                                             IProgress<LoadProgress> progress,
                                             CancellationToken cancellationToken);

        /// <summary>Nearest frame at or before a timestamp. Always 0 for a static source.</summary>
        int FrameIndexAtTime(double seconds);
    }

    /// <summary>
    /// Push model for live and streaming sources.
    ///
    /// VRS will implement both interfaces: this one for real-time playback, and
    /// <see cref="IPointCloudSource"/> for scrubbing, which needs random access.
    /// </summary>
    public interface IPointCloudStreamSource : IPointCloudSource
    {
        IAsyncEnumerable<PointCloudFrame> ReadFramesAsync(int startFrame, FrameRequest request,
                                                          CancellationToken cancellationToken);

        /// <summary>Raised by live sources as frames are appended.</summary>
        event Action<int> FrameCountChanged;
    }

    /// <summary>Creates sources for paths it recognises.</summary>
    public interface IPointCloudSourceFactory
    {
        string Id { get; }

        /// <summary>Lower-case extensions including the dot.</summary>
        string[] Extensions { get; }

        /// <summary>Higher wins when several factories claim a path.</summary>
        int Priority { get; }

        /// <summary>
        /// Decide from the path and the first bytes of the file. Content sniffing matters
        /// because a mis-named file should fail with "this is a PCD, not a PLY" rather than
        /// with a parse error a hundred bytes in.
        /// </summary>
        bool CanHandle(string path, ReadOnlySpan<byte> magic);

        IPointCloudSource Create(string path);
    }
}
