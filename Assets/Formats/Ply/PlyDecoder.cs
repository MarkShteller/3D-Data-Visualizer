using PointCloud.Core.Data;
using PointCloud.Core.Encoding;
using PointCloud.Formats.Common;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace PointCloud.Formats.Ply
{
    /// <summary>
    /// Where one source value lives. <see cref="Offset"/> is a byte offset within a record
    /// for binary files, and a whitespace-token index for ASCII ones.
    /// </summary>
    public struct PlySlot
    {
        public int Offset;
        public PlyScalarType Type;

        public bool IsValid => Type != PlyScalarType.None;
        public static PlySlot None => new() { Offset = -1, Type = PlyScalarType.None };
    }

    /// <summary>
    /// A fully resolved plan for decoding one vertex record. Flat and blittable so it can
    /// be handed straight to a Burst job.
    /// </summary>
    public struct PlyDecodeLayout
    {
        public int  Stride;          // bytes per record (binary) or token count (ascii)
        public bool BigEndian;

        public PlySlot X, Y, Z;
        public PlySlot R, G, B, A;
        public PlySlot NX, NY, NZ;
        public PlySlot Intensity, Label, Confidence, Timestamp;
        public PlySlot Scalar0, Scalar1, Scalar2, Scalar3;

        /// <summary>
        /// True when colour arrives as floats. uchar colour is 0-255 and sRGB-encoded;
        /// float colour is 0-1 and linear by Open3D/CloudCompare convention. Conflating the
        /// two makes every float-coloured cloud render washed out.
        /// </summary>
        public bool ColorIsFloat;

        /// <summary>Subtracted from source positions before the cast to float. See PointCloudDescriptor.</summary>
        public double3 OriginOffset;

        public bool HasPosition => X.IsValid && Y.IsValid && Z.IsValid;
        public bool HasColor    => R.IsValid && G.IsValid && B.IsValid;
        public bool HasNormal   => NX.IsValid && NY.IsValid && NZ.IsValid;
    }

    [BurstCompile]
    public static class PlyRead
    {
        /// <summary>
        /// Read one scalar from a binary record as a double.
        ///
        /// Unaligned loads: PLY records pack fields with no padding, so a float can land on
        /// any byte boundary. x64 handles unaligned access natively and Burst emits the
        /// unaligned form, which is why this reads through pointers rather than assembling
        /// byte by byte.
        /// </summary>
        public static unsafe double Scalar(byte* record, in PlySlot slot, bool bigEndian)
        {
            byte* p = record + slot.Offset;

            switch (slot.Type)
            {
                case PlyScalarType.Int8:  return *(sbyte*)p;
                case PlyScalarType.UInt8: return *p;

                case PlyScalarType.Int16:
                {
                    ushort raw = *(ushort*)p;
                    if (bigEndian) raw = Swap16(raw);
                    return (short)raw;
                }
                case PlyScalarType.UInt16:
                {
                    ushort raw = *(ushort*)p;
                    if (bigEndian) raw = Swap16(raw);
                    return raw;
                }
                case PlyScalarType.Int32:
                {
                    uint raw = *(uint*)p;
                    if (bigEndian) raw = Swap32(raw);
                    return (int)raw;
                }
                case PlyScalarType.UInt32:
                {
                    uint raw = *(uint*)p;
                    if (bigEndian) raw = Swap32(raw);
                    return raw;
                }
                case PlyScalarType.Float32:
                {
                    uint raw = *(uint*)p;
                    if (bigEndian) raw = Swap32(raw);
                    return math.asfloat(raw);
                }
                case PlyScalarType.Float64:
                {
                    ulong raw = *(ulong*)p;
                    if (bigEndian) raw = Swap64(raw);
                    return math.asdouble(raw);
                }
                default: return 0.0;
            }
        }

        public static ushort Swap16(ushort v) => (ushort)((v >> 8) | (v << 8));

        public static uint Swap32(uint v) =>
            ((v >> 24) & 0x000000FFu) | ((v >> 8) & 0x0000FF00u) |
            ((v << 8) & 0x00FF0000u)  | ((v << 24) & 0xFF000000u);

        public static ulong Swap64(ulong v) =>
            ((ulong)Swap32((uint)(v & 0xFFFFFFFFu)) << 32) | Swap32((uint)(v >> 32));

        /// <summary>Convert a raw colour channel to a 0-255 byte, honouring the float/byte convention.</summary>
        public static byte ColorByte(double raw, bool isFloat) =>
            (byte)math.clamp(math.round(isFloat ? raw * 255.0 : raw), 0.0, 255.0);
    }

    /// <summary>
    /// Decodes fixed-stride binary PLY records into the SoA attribute streams.
    ///
    /// One parallel pass over points, gathering, converting and byte-swapping in the same
    /// job so the record is touched exactly once. Absent attributes are still bound to
    /// one-element placeholders: the job safety system requires every NativeArray field to
    /// be constructed even on a branch the job never takes.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct PlyBinaryDecodeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Source;
        public PlyDecodeLayout Layout;
        public PointAttributes Mask;

        [WriteOnly] public NativeArray<float3> Positions;
        [WriteOnly] public NativeArray<uint>   Colors;
        [WriteOnly] public NativeArray<uint>   Normals;
        [WriteOnly] public NativeArray<uint>   Intensities;
        [WriteOnly] public NativeArray<uint>   Labels;
        [WriteOnly] public NativeArray<uint>   Confidences;
        [WriteOnly] public NativeArray<uint>   Timestamps;
        [WriteOnly] public NativeArray<uint>   Scalars0;
        [WriteOnly] public NativeArray<uint>   Scalars1;
        [WriteOnly] public NativeArray<uint>   Scalars2;
        [WriteOnly] public NativeArray<uint>   Scalars3;

        public void Execute(int i)
        {
            byte* record = (byte*)Source.GetUnsafeReadOnlyPtr() + (long)i * Layout.Stride;
            bool bigEndian = Layout.BigEndian;

            double x = PlyRead.Scalar(record, Layout.X, bigEndian);
            double y = PlyRead.Scalar(record, Layout.Y, bigEndian);
            double z = PlyRead.Scalar(record, Layout.Z, bigEndian);

            Positions[i] = (float3)(new double3(x, y, z) - Layout.OriginOffset);

            if ((Mask & PointAttributes.Color) != 0)
            {
                bool isFloat = Layout.ColorIsFloat;
                byte r = PlyRead.ColorByte(PlyRead.Scalar(record, Layout.R, bigEndian), isFloat);
                byte g = PlyRead.ColorByte(PlyRead.Scalar(record, Layout.G, bigEndian), isFloat);
                byte b = PlyRead.ColorByte(PlyRead.Scalar(record, Layout.B, bigEndian), isFloat);
                byte a = Layout.A.IsValid
                    ? PlyRead.ColorByte(PlyRead.Scalar(record, Layout.A, bigEndian), isFloat)
                    : (byte)255;

                Colors[i] = ColorPack.FromBytes(r, g, b, a);
            }

            if ((Mask & PointAttributes.Normal) != 0)
            {
                Normals[i] = OctNormal.Encode(new float3(
                    (float)PlyRead.Scalar(record, Layout.NX, bigEndian),
                    (float)PlyRead.Scalar(record, Layout.NY, bigEndian),
                    (float)PlyRead.Scalar(record, Layout.NZ, bigEndian)));
            }

            if ((Mask & PointAttributes.Intensity) != 0)
                Intensities[i] = math.asuint((float)PlyRead.Scalar(record, Layout.Intensity, bigEndian));

            // Label is an identifier, not a magnitude: reinterpret the integer rather than
            // storing its float bits, or the categorical palette indexes on nonsense.
            if ((Mask & PointAttributes.Label) != 0)
                Labels[i] = (uint)math.max(0.0, PlyRead.Scalar(record, Layout.Label, bigEndian));

            if ((Mask & PointAttributes.Confidence) != 0)
                Confidences[i] = math.asuint((float)PlyRead.Scalar(record, Layout.Confidence, bigEndian));

            if ((Mask & PointAttributes.Timestamp) != 0)
                Timestamps[i] = (uint)math.max(0.0, PlyRead.Scalar(record, Layout.Timestamp, bigEndian));

            if ((Mask & PointAttributes.Scalar0) != 0)
                Scalars0[i] = math.asuint((float)PlyRead.Scalar(record, Layout.Scalar0, bigEndian));
            if ((Mask & PointAttributes.Scalar1) != 0)
                Scalars1[i] = math.asuint((float)PlyRead.Scalar(record, Layout.Scalar1, bigEndian));
            if ((Mask & PointAttributes.Scalar2) != 0)
                Scalars2[i] = math.asuint((float)PlyRead.Scalar(record, Layout.Scalar2, bigEndian));
            if ((Mask & PointAttributes.Scalar3) != 0)
                Scalars3[i] = math.asuint((float)PlyRead.Scalar(record, Layout.Scalar3, bigEndian));
        }
    }

    /// <summary>
    /// Finds the byte offset of each vertex line in an ASCII PLY body.
    ///
    /// Sequential by necessity — line boundaries cannot be located without scanning — but
    /// it only touches each byte once and the parallel parse afterwards does the real work.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct PlyAsciiLineScanJob : IJob
    {
        [ReadOnly] public NativeArray<byte> Source;
        public int ExpectedLines;

        [WriteOnly] public NativeArray<int> LineStarts;
        [WriteOnly] public NativeArray<int> LineEnds;
        /// <summary>[0] receives the number of non-blank lines actually found.</summary>
        [WriteOnly] public NativeArray<int> FoundCount;

        public void Execute()
        {
            byte* buffer = (byte*)Source.GetUnsafeReadOnlyPtr();
            int length = Source.Length;

            int found = 0;
            int index = 0;

            while (index < length && found < ExpectedLines)
            {
                // Skip blank lines; some exporters pad the body with them.
                while (index < length && (buffer[index] == '\n' || buffer[index] == '\r')) index++;
                if (index >= length) break;

                int start = index;
                while (index < length && buffer[index] != '\n' && buffer[index] != '\r') index++;

                LineStarts[found] = start;
                LineEnds[found]   = index;
                found++;
            }

            FoundCount[0] = found;
        }
    }

    /// <summary>Parses one vertex per line into the SoA streams, in parallel.</summary>
    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct PlyAsciiDecodeJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<byte> Source;
        [ReadOnly] public NativeArray<int>  LineStarts;
        [ReadOnly] public NativeArray<int>  LineEnds;

        public PlyDecodeLayout Layout;
        public PointAttributes Mask;
        public int TokenCount;

        [WriteOnly] public NativeArray<float3> Positions;
        [WriteOnly] public NativeArray<uint>   Colors;
        [WriteOnly] public NativeArray<uint>   Normals;
        [WriteOnly] public NativeArray<uint>   Intensities;
        [WriteOnly] public NativeArray<uint>   Labels;
        [WriteOnly] public NativeArray<uint>   Confidences;
        [WriteOnly] public NativeArray<uint>   Timestamps;
        [WriteOnly] public NativeArray<uint>   Scalars0;
        [WriteOnly] public NativeArray<uint>   Scalars1;
        [WriteOnly] public NativeArray<uint>   Scalars2;
        [WriteOnly] public NativeArray<uint>   Scalars3;

        /// <summary>Scratch for one line's tokens. Allocator.Temp inside a job is per-thread.</summary>
        public void Execute(int i)
        {
            byte* buffer = (byte*)Source.GetUnsafeReadOnlyPtr();
            int index = LineStarts[i];
            int end   = LineEnds[i];

            var values = new NativeArray<double>(TokenCount, Allocator.Temp,
                                                 NativeArrayOptions.UninitializedMemory);

            for (int t = 0; t < TokenCount; t++)
            {
                if (!FastNumber.TryParseDouble(buffer, end, ref index, out double parsed))
                {
                    // A short or malformed line yields zeros for the missing tail rather than
                    // aborting the whole load; the source reports the count mismatch instead.
                    for (int rest = t; rest < TokenCount; rest++) values[rest] = 0.0;
                    break;
                }
                values[t] = parsed;
            }

            Positions[i] = (float3)(new double3(
                Get(values, Layout.X), Get(values, Layout.Y), Get(values, Layout.Z)) - Layout.OriginOffset);

            if ((Mask & PointAttributes.Color) != 0)
            {
                bool isFloat = Layout.ColorIsFloat;
                Colors[i] = ColorPack.FromBytes(
                    PlyRead.ColorByte(Get(values, Layout.R), isFloat),
                    PlyRead.ColorByte(Get(values, Layout.G), isFloat),
                    PlyRead.ColorByte(Get(values, Layout.B), isFloat),
                    Layout.A.IsValid ? PlyRead.ColorByte(Get(values, Layout.A), isFloat) : (byte)255);
            }

            if ((Mask & PointAttributes.Normal) != 0)
                Normals[i] = OctNormal.Encode(new float3(
                    (float)Get(values, Layout.NX),
                    (float)Get(values, Layout.NY),
                    (float)Get(values, Layout.NZ)));

            if ((Mask & PointAttributes.Intensity) != 0)
                Intensities[i] = math.asuint((float)Get(values, Layout.Intensity));
            if ((Mask & PointAttributes.Label) != 0)
                Labels[i] = (uint)math.max(0.0, Get(values, Layout.Label));
            if ((Mask & PointAttributes.Confidence) != 0)
                Confidences[i] = math.asuint((float)Get(values, Layout.Confidence));
            if ((Mask & PointAttributes.Timestamp) != 0)
                Timestamps[i] = (uint)math.max(0.0, Get(values, Layout.Timestamp));

            if ((Mask & PointAttributes.Scalar0) != 0) Scalars0[i] = math.asuint((float)Get(values, Layout.Scalar0));
            if ((Mask & PointAttributes.Scalar1) != 0) Scalars1[i] = math.asuint((float)Get(values, Layout.Scalar1));
            if ((Mask & PointAttributes.Scalar2) != 0) Scalars2[i] = math.asuint((float)Get(values, Layout.Scalar2));
            if ((Mask & PointAttributes.Scalar3) != 0) Scalars3[i] = math.asuint((float)Get(values, Layout.Scalar3));

            values.Dispose();
        }

        static double Get(NativeArray<double> values, in PlySlot slot) =>
            slot.IsValid && slot.Offset >= 0 && slot.Offset < values.Length ? values[slot.Offset] : 0.0;
    }
}
