using Unity.Burst;

namespace PointCloud.Formats.Common
{
    /// <summary>
    /// Culture-free number parsing straight out of a byte buffer.
    ///
    /// double.Parse is not an option on this path: it allocates, consults the current
    /// culture (so a machine with a comma decimal separator silently mis-reads every
    /// coordinate), and is roughly twenty times slower. A 20M-point ASCII PLY is about
    /// 1.4 GB of text, which turns minutes of difference.
    ///
    /// Accuracy: assembling the mantissa as an integer and applying one scaling factor
    /// keeps the result within an ulp or two of a correctly-rounded parse, which is far
    /// below the precision any sensor delivers.
    /// </summary>
    [BurstCompile]
    public static class FastNumber
    {
        /// <summary>
        /// Exact powers of ten up to 1e22, the largest representable without rounding.
        ///
        /// A switch rather than a static table: Burst cannot touch managed arrays, and every
        /// method here runs inside Burst-compiled decode jobs.
        /// </summary>
        static double PowerOfTen(int exponent) => exponent switch
        {
            0 => 1e0, 1 => 1e1, 2 => 1e2, 3 => 1e3, 4 => 1e4, 5 => 1e5,
            6 => 1e6, 7 => 1e7, 8 => 1e8, 9 => 1e9, 10 => 1e10, 11 => 1e11,
            12 => 1e12, 13 => 1e13, 14 => 1e14, 15 => 1e15, 16 => 1e16, 17 => 1e17,
            18 => 1e18, 19 => 1e19, 20 => 1e20, 21 => 1e21, _ => 1e22,
        };

        /// <summary>
        /// Parse a double starting at <paramref name="index"/>, advancing past it.
        /// Returns false on a malformed token, leaving the index at the offending byte.
        /// </summary>
        public static unsafe bool TryParseDouble(byte* buffer, int end, ref int index, out double value)
        {
            value = 0.0;

            SkipWhitespace(buffer, end, ref index);
            if (index >= end) return false;

            int start = index;
            bool negative = false;

            if (buffer[index] == '-') { negative = true; index++; }
            else if (buffer[index] == '+') index++;

            // NaN and Inf appear in organised clouds and in files from pipelines that emit
            // invalid returns rather than omitting them. They must parse, not fail.
            if (TryParseSpecial(buffer, end, ref index, negative, ref value)) return true;

            ulong mantissa = 0;
            int digits = 0, exponent = 0;
            bool any = false;

            while (index < end && buffer[index] >= (byte)'0' && buffer[index] <= (byte)'9')
            {
                // Stop accumulating once the mantissa would overflow; further digits only
                // shift the exponent and cannot change the value at double precision.
                if (mantissa < 1_000_000_000_000_000_000UL) { mantissa = mantissa * 10 + (ulong)(buffer[index] - '0'); digits++; }
                else exponent++;
                index++;
                any = true;
            }

            if (index < end && buffer[index] == '.')
            {
                index++;
                while (index < end && buffer[index] >= (byte)'0' && buffer[index] <= (byte)'9')
                {
                    if (mantissa < 1_000_000_000_000_000_000UL)
                    {
                        mantissa = mantissa * 10 + (ulong)(buffer[index] - '0');
                        digits++;
                        exponent--;
                    }
                    index++;
                    any = true;
                }
            }

            if (!any) { index = start; return false; }

            if (index < end && (buffer[index] == 'e' || buffer[index] == 'E'))
            {
                index++;
                bool exponentNegative = false;
                if (index < end && (buffer[index] == '-' || buffer[index] == '+'))
                {
                    exponentNegative = buffer[index] == '-';
                    index++;
                }

                int explicitExponent = 0;
                while (index < end && buffer[index] >= (byte)'0' && buffer[index] <= (byte)'9')
                {
                    explicitExponent = explicitExponent * 10 + (buffer[index] - '0');
                    if (explicitExponent > 4096) explicitExponent = 4096;   // clamp; result saturates anyway
                    index++;
                }
                exponent += exponentNegative ? -explicitExponent : explicitExponent;
            }

            value = Scale(mantissa, exponent);
            if (negative) value = -value;
            return true;
        }

        static double Scale(ulong mantissa, int exponent)
        {
            double result = mantissa;

            if (exponent > 0)
            {
                while (exponent > 22) { result *= 1e22; exponent -= 22; }
                result *= PowerOfTen(exponent);
            }
            else if (exponent < 0)
            {
                int e = -exponent;
                while (e > 22) { result /= 1e22; e -= 22; }
                result /= PowerOfTen(e);
            }
            return result;
        }

        /// <summary>
        /// NaN and Inf must parse rather than fail: organised clouds use them as invalid-return
        /// placeholders, and a parser that chokes on them rejects perfectly ordinary files.
        /// Byte comparisons rather than string ones, because Burst has no managed strings.
        /// </summary>
        static unsafe bool TryParseSpecial(byte* buffer, int end, ref int index, bool negative, ref double value)
        {
            if (index >= end) return false;

            if (Lower(buffer, end, index, 0) == 'n' &&
                Lower(buffer, end, index, 1) == 'a' &&
                Lower(buffer, end, index, 2) == 'n')
            {
                index += 3;
                value = double.NaN;
                return true;
            }

            if (Lower(buffer, end, index, 0) == 'i' &&
                Lower(buffer, end, index, 1) == 'n' &&
                Lower(buffer, end, index, 2) == 'f')
            {
                index += 3;

                // Consume the long spelling too.
                if (Lower(buffer, end, index, 0) == 'i' &&
                    Lower(buffer, end, index, 1) == 'n' &&
                    Lower(buffer, end, index, 2) == 'i' &&
                    Lower(buffer, end, index, 3) == 't' &&
                    Lower(buffer, end, index, 4) == 'y')
                    index += 5;

                value = negative ? double.NegativeInfinity : double.PositiveInfinity;
                return true;
            }

            return false;
        }

        /// <summary>Lower-cased byte at an offset, or 0 when past the end.</summary>
        static unsafe int Lower(byte* buffer, int end, int index, int offset)
        {
            int at = index + offset;
            return at < end ? buffer[at] | 0x20 : 0;
        }

        public static unsafe void SkipWhitespace(byte* buffer, int end, ref int index)
        {
            while (index < end)
            {
                byte c = buffer[index];
                if (c != (byte)' ' && c != (byte)'\t' && c != (byte)'\r' && c != (byte)'\n') return;
                index++;
            }
        }

        /// <summary>Advance past the current token without converting it.</summary>
        public static unsafe void SkipToken(byte* buffer, int end, ref int index)
        {
            SkipWhitespace(buffer, end, ref index);
            while (index < end)
            {
                byte c = buffer[index];
                if (c == (byte)' ' || c == (byte)'\t' || c == (byte)'\r' || c == (byte)'\n') return;
                index++;
            }
        }
    }
}
