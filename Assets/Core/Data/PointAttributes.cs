using System;
using System.Collections.Generic;

namespace PointCloud.Core.Data
{
    /// <summary>
    /// Which per-point attributes a cloud actually carries.
    ///
    /// Presence is the whole point of this type: a CV engineer needs to see that their
    /// exporter dropped the confidence channel, not just find the mode greyed out. The
    /// UI reads this mask directly to decide which render modes to offer and which to
    /// show disabled-with-a-reason.
    ///
    /// Bit positions are stable and used to index attribute stream arrays, so never
    /// renumber an existing member.
    /// </summary>
    [Flags]
    public enum PointAttributes : uint
    {
        None = 0,

        /// <summary>float3, 12 B. Always present, stored relative to Descriptor.OriginOffset.</summary>
        Position = 1u << 0,
        /// <summary>uint32 RGBA8. sRGB-encoded unless Descriptor.ColorIsLinear.</summary>
        Color = 1u << 1,
        /// <summary>uint32, octahedral-encoded unit vector. See OctNormal.</summary>
        Normal = 1u << 2,
        /// <summary>float. Lidar return strength / reflectance. Range is source-dependent.</summary>
        Intensity = 1u << 3,
        /// <summary>uint32. Semantic class, instance id, or segment id.</summary>
        Label = 1u << 4,
        /// <summary>float, nominally 0..1.</summary>
        Confidence = 1u << 5,
        /// <summary>uint32 microseconds relative to Descriptor.FrameTimeSeconds.</summary>
        Timestamp = 1u << 6,

        // Bit 7 intentionally unused — reserved so the scalar block starts on a nibble
        // boundary, which makes the mask readable in hex during debugging.

        /// <summary>float. Generic named scalar field; see Descriptor.ScalarFields[0].</summary>
        Scalar0 = 1u << 8,
        Scalar1 = 1u << 9,
        Scalar2 = 1u << 10,
        Scalar3 = 1u << 11,

        AnyScalar = Scalar0 | Scalar1 | Scalar2 | Scalar3,
    }

    /// <summary>
    /// What a generic scalar field means, when we can work it out from its source name.
    /// Drives the default colormap choice (a signed residual wants a diverging ramp;
    /// a distance wants a sequential one).
    /// </summary>
    public enum ScalarSemantic : byte
    {
        Unknown,
        Distance,
        Deviation,
        Curvature,
        Residual,
        Probability,
        Range,
        Angle,
        Count,
    }

    public enum PositionEncoding : byte
    {
        /// <summary>float3 relative to OriginOffset. The default: exact source bits survive.</summary>
        Float32,
        /// <summary>ushort3 quantised per chunk. Opt-in only — display-grade, not analysis-grade.</summary>
        QuantizedUShort3,
    }

    /// <summary>
    /// Static facts about each attribute. Kept beside the enum so a new attribute is one
    /// edit in one file.
    /// </summary>
    public static class PointAttributeInfo
    {
        /// <summary>Number of bit positions the enum uses; attribute stream arrays are this long.</summary>
        public const int SlotCount = 12;

        /// <summary>Bit position of a single-bit attribute, usable as an array index.</summary>
        public static int SlotOf(PointAttributes attribute)
        {
            uint v = (uint)attribute;
            if (v == 0 || (v & (v - 1)) != 0)
                throw new ArgumentException(
                    $"SlotOf expects exactly one attribute bit, got {attribute}.", nameof(attribute));

            int slot = 0;
            while ((v & 1u) == 0) { v >>= 1; slot++; }
            return slot;
        }

        /// <summary>Bytes per point for this attribute's stream.</summary>
        public static int ElementSize(PointAttributes attribute) =>
            attribute == PointAttributes.Position ? 12 : 4;

        /// <summary>Short lowercase name used in the UI and in log messages.</summary>
        public static string Name(PointAttributes attribute) => attribute switch
        {
            PointAttributes.Position   => "position",
            PointAttributes.Color      => "color",
            PointAttributes.Normal     => "normal",
            PointAttributes.Intensity  => "intensity",
            PointAttributes.Label      => "label",
            PointAttributes.Confidence => "confidence",
            PointAttributes.Timestamp  => "timestamp",
            PointAttributes.Scalar0    => "scalar0",
            PointAttributes.Scalar1    => "scalar1",
            PointAttributes.Scalar2    => "scalar2",
            PointAttributes.Scalar3    => "scalar3",
            _                          => attribute.ToString().ToLowerInvariant(),
        };

        /// <summary>Every single-bit attribute set in the mask, in bit order.</summary>
        public static IEnumerable<PointAttributes> Enumerate(PointAttributes mask)
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                var attribute = (PointAttributes)(1u << slot);
                if ((mask & attribute) != 0)
                    yield return attribute;
            }
        }

        /// <summary>Total bytes per point for a mask — the number the VRAM budget is built on.</summary>
        public static int BytesPerPoint(PointAttributes mask)
        {
            int total = 0;
            for (int slot = 0; slot < SlotCount; slot++)
            {
                var attribute = (PointAttributes)(1u << slot);
                if ((mask & attribute) != 0)
                    total += ElementSize(attribute);
            }
            return total;
        }

        /// <summary>The Scalar0..3 attribute for a scalar field index, or None if out of range.</summary>
        public static PointAttributes ScalarSlot(int index) =>
            index is >= 0 and < 4 ? (PointAttributes)((uint)PointAttributes.Scalar0 << index)
                                  : PointAttributes.None;
    }
}
