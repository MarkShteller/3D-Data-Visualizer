using PointCloud.Formats.Common;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;

namespace PointCloud.Formats.Ply
{
    /// <summary>
    /// Chooses the origin offset for clouds whose source positions are doubles.
    ///
    /// Geo-referenced data ships UTM eastings around 5e5 and northings around 4e6, where
    /// float32 has roughly 0.25-0.5 m of quantisation. Stored raw, such a cloud renders as
    /// a visibly wobbling mess and nobody can tell why. Subtracting a per-cloud origin puts
    /// coordinates near zero, where float32 has millimetre precision, and the descriptor
    /// keeps the offset so the inspector can still report the exact absolute coordinate.
    ///
    /// Only applied when the source really is double. A float source has already lost
    /// whatever precision it was going to lose, and re-centring it would change stored
    /// values for no gain — and break bit-exact comparison against the same cloud exported
    /// in another format.
    /// </summary>
    public static class PlyOriginOffset
    {
        /// <summary>Points sampled to locate the cloud. Enough to find the centre, cheap at any size.</summary>
        public const int SampleCount = 1024;

        public static unsafe double3 FromBinarySample(NativeArray<byte> body, in PlyDecodeLayout layout, int count)
        {
            if (count <= 0 || layout.Stride <= 0) return double3.zero;

            byte* buffer = (byte*)body.GetUnsafeReadOnlyPtr();
            long capacity = body.Length / layout.Stride;
            int usable = (int)math.min(count, capacity);
            if (usable <= 0) return double3.zero;

            int step = math.max(1, usable / SampleCount);

            double3 lo = double.PositiveInfinity;
            double3 hi = double.NegativeInfinity;
            bool any = false;

            for (int i = 0; i < usable; i += step)
            {
                byte* record = buffer + (long)i * layout.Stride;
                var p = new double3(
                    PlyRead.Scalar(record, layout.X, layout.BigEndian),
                    PlyRead.Scalar(record, layout.Y, layout.BigEndian),
                    PlyRead.Scalar(record, layout.Z, layout.BigEndian));

                if (math.any(math.isnan(p))) continue;
                lo = math.min(lo, p);
                hi = math.max(hi, p);
                any = true;
            }

            return any ? Quantize((lo + hi) * 0.5) : double3.zero;
        }

        public static unsafe double3 FromAsciiSample(NativeArray<byte> body,
                                                     NativeArray<int> lineStarts, NativeArray<int> lineEnds,
                                                     in PlyDecodeLayout layout, int tokenCount, int count)
        {
            if (count <= 0 || tokenCount <= 0) return double3.zero;

            byte* buffer = (byte*)body.GetUnsafeReadOnlyPtr();
            int usable = math.min(count, lineStarts.Length);
            int step = math.max(1, usable / SampleCount);

            // The three position tokens can sit anywhere in the line, so read up to the
            // furthest one and index into that.
            int needed = math.max(layout.X.Offset, math.max(layout.Y.Offset, layout.Z.Offset)) + 1;
            needed = math.min(needed, tokenCount);

            // Persistent: Temp is main-thread/job-worker only, and TempJob asserts a
            // 4-frame lifetime that a background-thread load routinely exceeds.
            var values = new NativeArray<double>(needed, Allocator.Persistent);

            double3 lo = double.PositiveInfinity;
            double3 hi = double.NegativeInfinity;
            bool any = false;

            for (int i = 0; i < usable; i += step)
            {
                int index = lineStarts[i];
                int end   = lineEnds[i];
                bool complete = true;

                for (int t = 0; t < needed; t++)
                {
                    if (!FastNumber.TryParseDouble(buffer, end, ref index, out double parsed))
                    {
                        complete = false;
                        break;
                    }
                    values[t] = parsed;
                }
                if (!complete) continue;

                var p = new double3(
                    Read(values, layout.X.Offset),
                    Read(values, layout.Y.Offset),
                    Read(values, layout.Z.Offset));

                if (math.any(math.isnan(p))) continue;
                lo = math.min(lo, p);
                hi = math.max(hi, p);
                any = true;
            }

            values.Dispose();
            return any ? Quantize((lo + hi) * 0.5) : double3.zero;

            static double Read(NativeArray<double> values, int index) =>
                index >= 0 && index < values.Length ? values[index] : 0.0;
        }

        /// <summary>
        /// Round the offset to whole metres.
        ///
        /// Two exports of the same scene rarely sample identically, and an offset that
        /// wobbles by centimetres between loads would make two copies of the same cloud fail
        /// to overlay. Rounding makes the offset reproducible for any sample of the same
        /// data, which is what overlay comparison depends on.
        /// </summary>
        static double3 Quantize(double3 centre) => math.round(centre);
    }
}
