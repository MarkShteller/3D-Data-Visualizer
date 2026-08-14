using NUnit.Framework;
using PointCloud.Core.Encoding;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace PointCloud.Tests.EditMode
{
    public class EncodingTests
    {
        [Test]
        public void OctNormal_RoundTrips_WithinHalfADegree()
        {
            var rng = new Random(0xC0FFEEu);
            const int samples = 100_000;

            double worstDegrees = 0.0;
            for (int i = 0; i < samples; i++)
            {
                // Uniform on the sphere, so the error is measured evenly over the encoding
                // domain rather than clustering near the poles where octahedral is best.
                float z   = rng.NextFloat(-1f, 1f);
                float phi = rng.NextFloat(0f, 2f * math.PI);
                float r   = math.sqrt(math.max(0f, 1f - z * z));
                float3 n  = new float3(r * math.cos(phi), r * math.sin(phi), z);

                float3 decoded = OctNormal.Decode(OctNormal.Encode(n));

                double degrees = math.degrees(math.acos(math.clamp(math.dot(n, decoded), -1f, 1f)));
                worstDegrees = math.max(worstDegrees, degrees);
            }

            Assert.Less(worstDegrees, 0.5,
                $"Worst octahedral round-trip error was {worstDegrees:F4} degrees over {samples} samples.");
        }

        [Test]
        public void OctNormal_HandlesAxisAlignedAndDegenerateInputs()
        {
            float3[] axes =
            {
                new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0),
                new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
            };

            foreach (var axis in axes)
            {
                var decoded = OctNormal.Decode(OctNormal.Encode(axis));
                Assert.Greater(math.dot(axis, decoded), 0.9999f, $"Axis {axis} did not survive the round trip.");
            }

            // A zero-length normal must not produce NaN — parsers do encounter these.
            var zero = OctNormal.Decode(OctNormal.Encode(float3.zero));
            Assert.IsFalse(math.any(math.isnan(zero)), "Zero-length normal decoded to NaN.");
            Assert.AreEqual(1f, math.length(zero), 1e-4f, "Zero-length normal did not decode to a unit vector.");
        }

        [Test]
        public void ColorPack_RoundTripsExactly()
        {
            var rng = new Random(0xBEEFu);

            for (int i = 0; i < 1_000_000; i++)
            {
                byte r = (byte)rng.NextInt(0, 256);
                byte g = (byte)rng.NextInt(0, 256);
                byte b = (byte)rng.NextInt(0, 256);
                byte a = (byte)rng.NextInt(0, 256);

                ColorPack.ToBytes(ColorPack.FromBytes(r, g, b, a),
                                  out byte r2, out byte g2, out byte b2, out byte a2);

                if (r != r2 || g != g2 || b != b2 || a != a2)
                    Assert.Fail($"Colour round trip failed: ({r},{g},{b},{a}) -> ({r2},{g2},{b2},{a2})");
            }
        }

        [Test]
        public void ColorPack_ByteLayoutIsRgbaLittleEndian()
        {
            // The parsers blit r,g,b,a bytes straight into the stream, so the packed uint
            // must have r in the low byte. If this flips, every colour comes out BGR.
            uint packed = ColorPack.FromBytes(0x11, 0x22, 0x33, 0x44);
            Assert.AreEqual(0x44332211u, packed);
        }

        [Test]
        public void SrgbConversion_MatchesKnownValues()
        {
            Assert.AreEqual(0f, ColorPack.SrgbToLinear(0f), 1e-6f);
            Assert.AreEqual(1f, ColorPack.SrgbToLinear(1f), 1e-6f);
            // Mid-grey 128/255 in sRGB is ~0.2159 linear. If this reads ~0.5, the conversion
            // is being skipped and every byte-coloured cloud will render washed out.
            Assert.AreEqual(0.2159f, ColorPack.SrgbToLinear(128f / 255f), 1e-3f);

            for (float c = 0f; c <= 1f; c += 0.05f)
                Assert.AreEqual(c, ColorPack.LinearToSrgb(ColorPack.SrgbToLinear(c)), 1e-4f);
        }
    }
}
