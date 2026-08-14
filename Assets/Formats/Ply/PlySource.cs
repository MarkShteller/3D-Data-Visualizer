using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PointCloud.Core.Data;
using PointCloud.Core.Diagnostics;
using PointCloud.Core.Sources;
using PointCloud.Core.Spatial;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace PointCloud.Formats.Ply
{
    public sealed class PlySourceFactory : IPointCloudSourceFactory
    {
        readonly LoadLog _log;

        public PlySourceFactory(LoadLog log = null) => _log = log;

        public string   Id => "ply";
        public string[] Extensions => new[] { ".ply" };
        public int      Priority => 100;

        public bool CanHandle(string path, ReadOnlySpan<byte> magic)
        {
            // Content first: a mis-named .txt that is really a PLY should still open, and a
            // .ply that is really something else should be rejected here rather than
            // failing obscurely mid-parse.
            if (magic.Length >= 3 && magic[0] == 'p' && magic[1] == 'l' && magic[2] == 'y')
                return true;

            return magic.Length == 0 && SourceRegistry.HasExtension(path, ".ply");
        }

        public IPointCloudSource Create(string path) => new PlySource(path, _log);
    }

    /// <summary>
    /// Reads Stanford PLY: ascii, binary_little_endian and binary_big_endian, with whatever
    /// per-vertex properties the file happens to carry.
    /// </summary>
    public sealed class PlySource : IPointCloudSource
    {
        /// <summary>
        /// Vertex blocks larger than this are refused rather than attempted.
        ///
        /// The whole block is currently read into memory before decoding, so a genuinely
        /// huge file would OOM. Slab-wise streaming is the fix; until then this fails with
        /// a message that says what happened instead of taking the editor down with it.
        /// </summary>
        public const long MaxVertexBlockBytes = 6L * 1024 * 1024 * 1024;

        readonly string  _path;
        readonly LoadLog _log;

        PlyHeader     _header;
        PlyLayoutPlan _plan;

        public PlySource(string path, LoadLog log = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _log  = log;
        }

        public string Id => "ply";
        public string DisplayName => Path.GetFileName(_path);

        public SourceCapabilities Capabilities =>
            SourceCapabilities.RandomAccess | SourceCapabilities.KnownFrameCount |
            SourceCapabilities.ProgressBytes | SourceCapabilities.Cancellable;

        public PointCloudDescriptor Metadata { get; private set; }
        public int    FrameCount => 1;
        public double DurationSeconds => 0.0;

        public int FrameIndexAtTime(double seconds) => 0;

        public Task OpenAsync(IProgress<LoadProgress> progress, CancellationToken cancellationToken)
        {
            // Task.Run because this is blocking file IO, which has no business occupying a
            // Job System worker.
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LoadProgress(LoadPhase.Opening, DisplayName));

                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

                progress?.Report(new LoadProgress(LoadPhase.ParsingHeader, "reading header"));
                _header = PlyHeaderParser.Parse(stream);

                var vertex = _header.Vertex;
                if (vertex == null)
                    throw new PointCloudFormatException("ply",
                        "No 'vertex' element; this PLY contains no points.", _header.DataOffset);

                if (vertex.Count > int.MaxValue)
                    throw new PointCloudFormatException("ply",
                        $"{vertex.Count:N0} vertices exceeds the {int.MaxValue:N0} this tool can address.");

                _plan = PlyLayoutBuilder.Build(vertex, _header.Format, (PointAttributes)0xFFFFFFFF);

                Metadata = new PointCloudDescriptor
                {
                    Name          = Path.GetFileNameWithoutExtension(_path),
                    SourcePath    = _path,
                    FormatId      = "ply",
                    PointCount    = (int)vertex.Count,
                    Attributes    = _plan.Attributes,
                    ColorIsLinear = _plan.Layout.ColorIsFloat,
                    ScalarFields  = _plan.ScalarFields,
                };

                foreach (var note in _plan.Notes) _log?.Info("PLY", $"{DisplayName}: {note}");
                _log?.Info("PLY", $"{DisplayName}: {_header.Format}, {vertex.Count:N0} vertices, " +
                                  $"{vertex.Properties.Count} properties, stride {_plan.Layout.Stride} B");

                progress?.Report(new LoadProgress(LoadPhase.Complete, "header parsed"));
            }, cancellationToken);
        }

        public Task<PointCloudFrame> ReadFrameAsync(int frameIndex, FrameRequest request,
                                                    IProgress<LoadProgress> progress,
                                                    CancellationToken cancellationToken)
        {
            if (_header == null)
                throw new InvalidOperationException("OpenAsync must complete before ReadFrameAsync.");

            return Task.Run(() => ReadFrame(request, progress, cancellationToken), cancellationToken);
        }

        PointCloudFrame ReadFrame(FrameRequest request, IProgress<LoadProgress> progress,
                                  CancellationToken cancellationToken)
        {
            var vertex = _header.Vertex;
            int count = (int)vertex.Count;

            // Rebuild the plan against what the caller actually wants, so unwanted properties
            // never allocate a stream.
            var plan = PlyLayoutBuilder.Build(vertex, _header.Format, request.Wanted);
            var attributes = plan.Attributes;

            var descriptor = new PointCloudDescriptor
            {
                Name          = Path.GetFileNameWithoutExtension(_path),
                SourcePath    = _path,
                FormatId      = "ply",
                PointCount    = count,
                Attributes    = PointAttributes.Position,
                ColorIsLinear = plan.Layout.ColorIsFloat,
                ScalarFields  = plan.ScalarFields,
            };

            var data = new PointCloudData(descriptor);
            NativeArray<byte> body = default;
            var placeholders = new List<NativeArray<uint>>(11);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            double readMs, decodeMs, chunkMs, rangeMs;

            try
            {
                body = ReadBody(vertex, plan, count, progress, cancellationToken);
                readMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new LoadProgress(LoadPhase.Decoding, "decoding points",
                                                  pointsRead: 0, pointsTotal: count));

                var positions = data.AddStream(PointAttributes.Position, Allocator.Persistent).As<float3>();

                var colors      = Stream(data, attributes, PointAttributes.Color, placeholders);
                var normals     = Stream(data, attributes, PointAttributes.Normal, placeholders);
                var intensities = Stream(data, attributes, PointAttributes.Intensity, placeholders);
                var labels      = Stream(data, attributes, PointAttributes.Label, placeholders);
                var confidences = Stream(data, attributes, PointAttributes.Confidence, placeholders);
                var timestamps  = Stream(data, attributes, PointAttributes.Timestamp, placeholders);
                var scalars0    = Stream(data, attributes, PointAttributes.Scalar0, placeholders);
                var scalars1    = Stream(data, attributes, PointAttributes.Scalar1, placeholders);
                var scalars2    = Stream(data, attributes, PointAttributes.Scalar2, placeholders);
                var scalars3    = Stream(data, attributes, PointAttributes.Scalar3, placeholders);

                if (_header.Format == PlyFormat.Ascii)
                    DecodeAscii(body, plan, attributes, count, positions, colors, normals, intensities,
                                labels, confidences, timestamps, scalars0, scalars1, scalars2, scalars3,
                                cancellationToken);
                else
                    DecodeBinary(body, plan, attributes, count, positions, colors, normals, intensities,
                                 labels, confidences, timestamps, scalars0, scalars1, scalars2, scalars3);

                descriptor.OriginOffset = plan.Layout.OriginOffset;
                decodeMs = timer.Elapsed.TotalMilliseconds; timer.Restart();
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new LoadProgress(LoadPhase.BuildingChunks, "spatial ordering",
                                                  pointsRead: count, pointsTotal: count));

                int chunkSize = request.ChunkSize > 0 ? request.ChunkSize : ChunkBuilder.DefaultChunkSize;
                ChunkBuilder.Build(data, out var chunkStats, chunkSize);
                chunkMs = timer.Elapsed.TotalMilliseconds; timer.Restart();

                descriptor.MedianPointSpacing = EstimateSpacing(descriptor.LocalBounds, count);
                PopulateScalarRanges(data, descriptor);
                rangeMs = timer.Elapsed.TotalMilliseconds;

                // Stage timings, not just a total: "opening is slow" is only actionable if
                // the log says which stage.
                _log?.Info("PLY",
                    $"{DisplayName}: read {readMs:F0} ms, decode {decodeMs:F0} ms, " +
                    $"chunk {chunkMs:F0} ms, ranges {rangeMs:F0} ms  [{chunkStats}]");

                progress?.Report(new LoadProgress(LoadPhase.Complete, "loaded",
                                                  pointsRead: count, pointsTotal: count));

                return new PointCloudFrame(data);
            }
            catch
            {
                // Every native allocation made so far must go back, or a cancelled load
                // leaks into the next play session — domain reload will not clean it up.
                data.Dispose();
                throw;
            }
            finally
            {
                if (body.IsCreated) body.Dispose();
                foreach (var placeholder in placeholders) placeholder.Dispose();
            }
        }

        /// <summary>
        /// Read the vertex block. Elements declared before 'vertex' are walked past; anything
        /// after it is ignored entirely, which is what lets a mesh PLY with a trailing face
        /// list load as a point cloud.
        /// </summary>
        NativeArray<byte> ReadBody(PlyElement vertex, PlyLayoutPlan plan, int count,
                                   IProgress<LoadProgress> progress, CancellationToken cancellationToken)
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              bufferSize: 1 << 20, FileOptions.SequentialScan);
            stream.Seek(_header.DataOffset, SeekOrigin.Begin);

            SkipElementsBefore(stream, vertex, cancellationToken);

            long available = stream.Length - stream.Position;
            long wanted = _header.Format == PlyFormat.Ascii
                ? available                                    // ASCII length is not predictable
                : (long)plan.Layout.Stride * count;

            if (wanted > MaxVertexBlockBytes)
                throw new PointCloudFormatException("ply",
                    $"Vertex block is {wanted / (1024 * 1024)} MB, above the " +
                    $"{MaxVertexBlockBytes / (1024 * 1024)} MB this build can load in one piece.");

            if (_header.Format != PlyFormat.Ascii && wanted > available)
                throw new PointCloudFormatException("ply",
                    $"Header declares {count:N0} vertices ({wanted:N0} bytes) but only " +
                    $"{available:N0} bytes remain. The file is truncated.", stream.Position);

            long total = Math.Min(wanted, available);
            var body = new NativeArray<byte>((int)total, Allocator.Persistent,
                                             NativeArrayOptions.UninitializedMemory);

            try
            {
                var slab = new byte[1 << 20];
                long read = 0;

                while (read < total)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int want = (int)Math.Min(slab.Length, total - read);
                    int got = stream.Read(slab, 0, want);
                    if (got <= 0)
                        throw new PointCloudFormatException("ply",
                            $"File ended after {read:N0} of {total:N0} expected data bytes.",
                            _header.DataOffset + read);

                    NativeArray<byte>.Copy(slab, 0, body, (int)read, got);
                    read += got;

                    progress?.Report(new LoadProgress(LoadPhase.ReadingData, "reading",
                                                      bytesRead: read, bytesTotal: total));
                }

                return body;
            }
            catch
            {
                body.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Walk past elements declared before 'vertex'. Fixed-stride elements are a seek;
        /// a list-bearing element has no fixed size and has to be stepped record by record.
        /// </summary>
        void SkipElementsBefore(Stream stream, PlyElement vertex, CancellationToken cancellationToken)
        {
            foreach (var element in _header.Elements)
            {
                if (ReferenceEquals(element, vertex)) return;
                cancellationToken.ThrowIfCancellationRequested();

                if (_header.Format == PlyFormat.Ascii)
                    throw new PointCloudUnsupportedException("ply",
                        $"ASCII PLY with element '{element.Name}' declared before 'vertex' is not supported. " +
                        "Re-export with vertices first.");

                if (element.HasFixedStride)
                {
                    stream.Seek((long)element.Stride * element.Count, SeekOrigin.Current);
                    continue;
                }

                throw new PointCloudUnsupportedException("ply",
                    $"Element '{element.Name}' contains list properties and is declared before " +
                    "'vertex', so the vertex data cannot be located. Re-export with vertices first.");
            }
        }

        void DecodeBinary(NativeArray<byte> body, PlyLayoutPlan plan, PointAttributes attributes, int count,
                          NativeArray<float3> positions, NativeArray<uint> colors, NativeArray<uint> normals,
                          NativeArray<uint> intensities, NativeArray<uint> labels, NativeArray<uint> confidences,
                          NativeArray<uint> timestamps, NativeArray<uint> scalars0, NativeArray<uint> scalars1,
                          NativeArray<uint> scalars2, NativeArray<uint> scalars3)
        {
            if (plan.PositionsAreDouble)
                plan.Layout.OriginOffset = PlyOriginOffset.FromBinarySample(body, plan.Layout, count);

            new PlyBinaryDecodeJob
            {
                Source = body, Layout = plan.Layout, Mask = attributes,
                Positions = positions, Colors = colors, Normals = normals, Intensities = intensities,
                Labels = labels, Confidences = confidences, Timestamps = timestamps,
                Scalars0 = scalars0, Scalars1 = scalars1, Scalars2 = scalars2, Scalars3 = scalars3,
            }.Schedule(count, 4096).Complete();
        }

        void DecodeAscii(NativeArray<byte> body, PlyLayoutPlan plan, PointAttributes attributes, int count,
                         NativeArray<float3> positions, NativeArray<uint> colors, NativeArray<uint> normals,
                         NativeArray<uint> intensities, NativeArray<uint> labels, NativeArray<uint> confidences,
                         NativeArray<uint> timestamps, NativeArray<uint> scalars0, NativeArray<uint> scalars1,
                         NativeArray<uint> scalars2, NativeArray<uint> scalars3,
                         CancellationToken cancellationToken)
        {
            // Persistent rather than TempJob: this work spans an unbounded number of
            // frames on a background thread, which trips TempJob's 4-frame assertion.
            var lineStarts = new NativeArray<int>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var lineEnds   = new NativeArray<int>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            var found      = new NativeArray<int>(1, Allocator.Persistent);

            try
            {
                new PlyAsciiLineScanJob
                {
                    Source = body, ExpectedLines = count,
                    LineStarts = lineStarts, LineEnds = lineEnds, FoundCount = found,
                }.Schedule().Complete();

                if (found[0] < count)
                    throw new PointCloudFormatException("ply",
                        $"Header declares {count:N0} vertices but only {found[0]:N0} data lines were found.",
                        _header.DataOffset);

                cancellationToken.ThrowIfCancellationRequested();

                if (plan.PositionsAreDouble)
                    plan.Layout.OriginOffset =
                        PlyOriginOffset.FromAsciiSample(body, lineStarts, lineEnds, plan.Layout,
                                                        plan.TokenCount, count);

                new PlyAsciiDecodeJob
                {
                    Source = body, LineStarts = lineStarts, LineEnds = lineEnds,
                    Layout = plan.Layout, Mask = attributes, TokenCount = plan.TokenCount,
                    Positions = positions, Colors = colors, Normals = normals, Intensities = intensities,
                    Labels = labels, Confidences = confidences, Timestamps = timestamps,
                    Scalars0 = scalars0, Scalars1 = scalars1, Scalars2 = scalars2, Scalars3 = scalars3,
                }.Schedule(count, 2048).Complete();
            }
            finally
            {
                lineStarts.Dispose();
                lineEnds.Dispose();
                found.Dispose();
            }
        }

        static NativeArray<uint> Stream(PointCloudData data, PointAttributes attributes,
                                        PointAttributes attribute, List<NativeArray<uint>> placeholders)
        {
            if ((attributes & attribute) == 0)
            {
                // Job fields must be constructed even on branches the job never takes.
                // Persistent, not TempJob: TempJob asserts a 4-frame lifetime, and a load
                // runs on a background thread for as long as the file takes. Disposed in
                // the finally below either way.
                var placeholder = new NativeArray<uint>(1, Allocator.Persistent);
                placeholders.Add(placeholder);
                return placeholder;
            }
            return data.AddStream(attribute, Allocator.Persistent).As<uint>();
        }

        /// <summary>
        /// Rough spacing from bounds and count, used to seed the default point radius.
        /// A proper median nearest-neighbour estimate would be better and is what the
        /// synthetic generator does analytically; this is close enough that a file opens
        /// looking solid rather than like static.
        /// </summary>
        static float EstimateSpacing(UnityEngine.Bounds bounds, int count)
        {
            if (count <= 1) return 0.01f;

            var size = (float3)(UnityEngine.Vector3)bounds.size;
            float3 sorted = math.max(size, 0f);

            // Treat a near-flat cloud as a surface rather than a volume, or the estimate
            // collapses toward zero and every point renders sub-pixel.
            float extent = math.cmax(sorted);
            if (extent <= 0f) return 0.01f;

            float thin = math.cmin(sorted);
            if (thin < extent * 1e-3f)
            {
                float area = 1f;
                int axes = 0;
                for (int i = 0; i < 3; i++)
                    if (sorted[i] >= extent * 1e-3f) { area *= sorted[i]; axes++; }
                if (axes == 0) return extent / math.max(2f, math.sqrt(count));
                return math.sqrt(area / count);
            }

            return math.pow(sorted.x * sorted.y * sorted.z / count, 1f / 3f);
        }

        static void PopulateScalarRanges(PointCloudData data, PointCloudDescriptor descriptor)
        {
            if (descriptor.ScalarFields == null) return;

            for (int i = 0; i < descriptor.ScalarFields.Length; i++)
            {
                var attribute = PointAttributeInfo.ScalarSlot(i);
                if (!data.TryGet(attribute, out var stream)) continue;

                var values = stream.As<uint>();
                float lo = float.PositiveInfinity, hi = float.NegativeInfinity;

                // Stride the scan: an exact range over 20M values costs more than it is worth
                // for a display default the user can refit with one click.
                int step = math.max(1, values.Length / 200_000);
                for (int v = 0; v < values.Length; v += step)
                {
                    float value = math.asfloat(values[v]);
                    if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                    lo = math.min(lo, value);
                    hi = math.max(hi, value);
                }

                if (float.IsInfinity(lo) || float.IsInfinity(hi)) { lo = 0f; hi = 1f; }
                if (hi <= lo) hi = lo + 1e-6f;

                descriptor.ScalarFields[i].SourceRange  = new float2(lo, hi);
                descriptor.ScalarFields[i].DisplayRange = new float2(lo, hi);
            }
        }

        public ValueTask DisposeAsync()
        {
            _header = null;
            _plan = null;
            return default;
        }
    }
}
