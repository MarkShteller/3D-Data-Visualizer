using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PointCloud.Core.Data;
using PointCloud.Core.Diagnostics;
using PointCloud.Core.Sources;

namespace PointCloud.Formats.Vrs
{
    /// <summary>
    /// Placeholder for VRS, registered so the format is discoverable now.
    ///
    /// Deliberately not silence: a .vrs file resolves to a real factory, opens through the
    /// same code path as every other format, and fails with a message that says the support
    /// is not built yet rather than "unsupported file". That means the discovery, error and
    /// UI paths are exercised from day one, and phase 2 is a single class swap rather than
    /// a new seam through the loader.
    /// </summary>
    public sealed class VrsSourceFactory : IPointCloudSourceFactory
    {
        /// <summary>VRS files begin with a tagged 4-byte magic followed by a format version.</summary>
        static readonly byte[] Magic = { 0x56, 0x69, 0x73, 0x69 };   // "Visi"

        public string   Id => "vrs";
        public string[] Extensions => new[] { ".vrs" };

        // Below PLY and friends: only claim a file nothing else wants.
        public int Priority => 10;

        public bool CanHandle(string path, ReadOnlySpan<byte> magic)
        {
            if (SourceRegistry.HasExtension(path, ".vrs")) return true;

            if (magic.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (magic[i] != Magic[i]) return false;
            return true;
        }

        public IPointCloudSource Create(string path) => new VrsSourceStub(path);
    }

    public sealed class VrsSourceStub : IPointCloudSource
    {
        readonly string _path;

        public VrsSourceStub(string path) => _path = path;

        public string Id => "vrs";
        public string DisplayName => Path.GetFileName(_path);

        /// <summary>
        /// Declared as it will actually behave once implemented, so any UI built against
        /// these flags is already correct when the real reader lands.
        /// </summary>
        public SourceCapabilities Capabilities =>
            SourceCapabilities.Sequenced | SourceCapabilities.RandomAccess |
            SourceCapabilities.Poses | SourceCapabilities.Cancellable;

        public PointCloudDescriptor Metadata => null;
        public int    FrameCount => 0;
        public double DurationSeconds => 0.0;

        public int FrameIndexAtTime(double seconds) => 0;

        public Task OpenAsync(IProgress<LoadProgress> progress, CancellationToken cancellationToken)
        {
            progress?.Report(new LoadProgress(LoadPhase.Failed, "VRS is not supported yet"));

            throw new PointCloudUnsupportedException("vrs",
                $"'{DisplayName}' is a VRS recording, which this build cannot read yet. " +
                "VRS support is planned for the next phase; for now, export the point " +
                "stream to PLY or PCD.");
        }

        public Task<PointCloudFrame> ReadFrameAsync(int frameIndex, FrameRequest request,
                                                     IProgress<LoadProgress> progress,
                                                     CancellationToken cancellationToken) =>
            throw new PointCloudUnsupportedException("vrs", "VRS support is not implemented yet.");

        public ValueTask DisposeAsync() => default;
    }
}
