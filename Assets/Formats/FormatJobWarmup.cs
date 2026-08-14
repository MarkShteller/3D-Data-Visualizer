using PointCloud.Core.Data;
using PointCloud.Formats.Ply;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace PointCloud.Formats
{
    /// <summary>
    /// Compiles the format decode jobs on the main thread, for the same reason as
    /// <see cref="PointCloud.Core.JobWarmup"/>: a job first invoked from a background thread
    /// never gets Burst-compiled, and parsing always runs on Task.Run.
    ///
    /// Each job is run once over a single synthetic record. The results are discarded — only
    /// the compilation is wanted.
    /// </summary>
    public static class FormatJobWarmup
    {
        public static void Run()
        {
            WarmPlyBinary();
            WarmPlyAscii();
        }

        static PlyDecodeLayout SingleRecordLayout(bool ascii)
        {
            // Three float positions, addressed by byte offset for binary and by token index
            // for ASCII, matching how the real layouts are built.
            return new PlyDecodeLayout
            {
                Stride = ascii ? 0 : 12,
                X = new PlySlot { Offset = ascii ? 0 : 0, Type = PlyScalarType.Float32 },
                Y = new PlySlot { Offset = ascii ? 1 : 4, Type = PlyScalarType.Float32 },
                Z = new PlySlot { Offset = ascii ? 2 : 8, Type = PlyScalarType.Float32 },
                R = PlySlot.None, G = PlySlot.None, B = PlySlot.None, A = PlySlot.None,
                NX = PlySlot.None, NY = PlySlot.None, NZ = PlySlot.None,
                Intensity = PlySlot.None, Label = PlySlot.None,
                Confidence = PlySlot.None, Timestamp = PlySlot.None,
                Scalar0 = PlySlot.None, Scalar1 = PlySlot.None,
                Scalar2 = PlySlot.None, Scalar3 = PlySlot.None,
            };
        }

        static void WarmPlyBinary()
        {
            var source = new NativeArray<byte>(12, Allocator.TempJob);
            var positions = new NativeArray<float3>(1, Allocator.TempJob);
            var scratch = Scratch(11);

            try
            {
                new PlyBinaryDecodeJob
                {
                    Source = source,
                    Layout = SingleRecordLayout(ascii: false),
                    Mask = PointAttributes.Position,
                    Positions = positions,
                    Colors = scratch[0], Normals = scratch[1], Intensities = scratch[2],
                    Labels = scratch[3], Confidences = scratch[4], Timestamps = scratch[5],
                    Scalars0 = scratch[6], Scalars1 = scratch[7],
                    Scalars2 = scratch[8], Scalars3 = scratch[9],
                }.Schedule(1, 1).Complete();
            }
            finally
            {
                source.Dispose();
                positions.Dispose();
                foreach (var array in scratch) array.Dispose();
            }
        }

        static void WarmPlyAscii()
        {
            // "0 0 0\n" — one parseable record.
            var source = new NativeArray<byte>(6, Allocator.TempJob);
            source[0] = (byte)'0'; source[1] = (byte)' ';
            source[2] = (byte)'0'; source[3] = (byte)' ';
            source[4] = (byte)'0'; source[5] = (byte)'\n';

            var lineStarts = new NativeArray<int>(1, Allocator.TempJob);
            var lineEnds   = new NativeArray<int>(1, Allocator.TempJob);
            var found      = new NativeArray<int>(1, Allocator.TempJob);
            var positions  = new NativeArray<float3>(1, Allocator.TempJob);
            var scratch    = Scratch(11);

            try
            {
                new PlyAsciiLineScanJob
                {
                    Source = source, ExpectedLines = 1,
                    LineStarts = lineStarts, LineEnds = lineEnds, FoundCount = found,
                }.Schedule().Complete();

                new PlyAsciiDecodeJob
                {
                    Source = source, LineStarts = lineStarts, LineEnds = lineEnds,
                    Layout = SingleRecordLayout(ascii: true),
                    Mask = PointAttributes.Position,
                    TokenCount = 3,
                    Positions = positions,
                    Colors = scratch[0], Normals = scratch[1], Intensities = scratch[2],
                    Labels = scratch[3], Confidences = scratch[4], Timestamps = scratch[5],
                    Scalars0 = scratch[6], Scalars1 = scratch[7],
                    Scalars2 = scratch[8], Scalars3 = scratch[9],
                }.Schedule(1, 1).Complete();
            }
            finally
            {
                source.Dispose();
                lineStarts.Dispose();
                lineEnds.Dispose();
                found.Dispose();
                positions.Dispose();
                foreach (var array in scratch) array.Dispose();
            }
        }

        /// <summary>
        /// Placeholders for the attribute streams the warm-up does not exercise. Job fields
        /// must be constructed even on branches the job never takes.
        /// </summary>
        static NativeArray<uint>[] Scratch(int count)
        {
            var arrays = new NativeArray<uint>[count];
            for (int i = 0; i < count; i++) arrays[i] = new NativeArray<uint>(1, Allocator.TempJob);
            return arrays;
        }
    }
}
