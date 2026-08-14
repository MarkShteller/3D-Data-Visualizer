using System;
using Unity.Collections;
using Unity.Mathematics;

namespace PointCloud.Core.Data
{
    /// <summary>How much of a cloud stays resident on the CPU after upload.</summary>
    public enum CpuRetention : byte
    {
        /// <summary>Free everything after upload. Picking falls back to the GPU path.</summary>
        None,
        /// <summary>Keep positions (12 B/pt) for CPU picking and the spatial index. Default.</summary>
        PositionsOnly,
        /// <summary>Keep every stream. Needed for subset export and CPU-side histograms.</summary>
        All,
    }

    /// <summary>
    /// A loaded point cloud in structure-of-arrays form: one flat stream per present
    /// attribute, plus the chunk table describing spatially-local runs within them.
    ///
    /// SoA rather than an interleaved struct because attributes are optional (an
    /// interleaved layout would force a worst-case stride even for an XYZ-only cloud) and
    /// because each render mode reads only the streams it needs. Consecutive point indices
    /// stay consecutive in every stream, so GPU loads remain fully coalesced.
    /// </summary>
    public sealed class PointCloudData : IDisposable
    {
        readonly AttributeStream[] _streams = new AttributeStream[PointAttributeInfo.SlotCount];
        bool _disposed;

        public PointCloudDescriptor Descriptor { get; }

        /// <summary>Chunk table, ordered by Start. Always covers [0, PointCount) with no gaps.</summary>
        public NativeArray<PointChunk> Chunks;

        public CpuRetention Retention = CpuRetention.PositionsOnly;

        public int PointCount => Descriptor.PointCount;

        public PointCloudData(PointCloudDescriptor descriptor)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        }

        /// <summary>
        /// Allocate the stream for an attribute and record it in the descriptor's mask.
        /// Throws if the attribute already has a stream.
        /// </summary>
        public AttributeStream AddStream(PointAttributes attribute, Allocator allocator)
        {
            int slot = PointAttributeInfo.SlotOf(attribute);
            if (_streams[slot].IsCreated)
                throw new InvalidOperationException(
                    $"{PointAttributeInfo.Name(attribute)} stream already exists on '{Descriptor.Name}'.");

            var stream = AttributeStream.Allocate(attribute, PointCount, allocator);
            _streams[slot] = stream;
            Descriptor.Attributes |= attribute;
            return stream;
        }

        public bool TryGet(PointAttributes attribute, out AttributeStream stream)
        {
            stream = _streams[PointAttributeInfo.SlotOf(attribute)];
            return stream.IsCreated;
        }

        public AttributeStream Get(PointAttributes attribute)
        {
            if (!TryGet(attribute, out var stream))
                throw new InvalidOperationException(
                    $"'{Descriptor.Name}' has no {PointAttributeInfo.Name(attribute)} stream.");
            return stream;
        }

        public NativeArray<float3> Positions => Get(PointAttributes.Position).As<float3>();

        /// <summary>
        /// Swap in a replacement stream for an attribute that already has one, disposing the
        /// old data. Used by ChunkBuilder, which reorders every stream through the Morton
        /// permutation and cannot do it in place.
        /// </summary>
        internal void ReplaceStream(PointAttributes attribute, AttributeStream replacement)
        {
            int slot = PointAttributeInfo.SlotOf(attribute);
            if (!_streams[slot].IsCreated)
                throw new InvalidOperationException(
                    $"No existing {PointAttributeInfo.Name(attribute)} stream to replace on '{Descriptor.Name}'.");

            _streams[slot].Dispose();
            _streams[slot] = replacement;
        }

        /// <summary>Every allocated stream, for bulk operations such as the chunk-order gather.</summary>
        internal AttributeStream[] StreamsInternal => _streams;

        /// <summary>
        /// Free the streams a given retention level does not need. Called after GPU upload.
        /// Position is kept whenever retention is not None because both the CPU picker and
        /// the spatial index need it, and it is the same 12 B/pt either way.
        /// </summary>
        public void ApplyRetention(CpuRetention retention)
        {
            Retention = retention;
            if (retention == CpuRetention.All) return;

            for (int slot = 0; slot < _streams.Length; slot++)
            {
                var attribute = (PointAttributes)(1u << slot);
                bool keep = retention == CpuRetention.PositionsOnly && attribute == PointAttributes.Position;
                if (keep || !_streams[slot].IsCreated) continue;

                _streams[slot].Dispose();
                _streams[slot] = default;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int i = 0; i < _streams.Length; i++)
            {
                _streams[i].Dispose();
                _streams[i] = default;
            }
            if (Chunks.IsCreated) Chunks.Dispose();
            Chunks = default;
        }
    }
}
