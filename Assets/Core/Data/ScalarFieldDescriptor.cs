using Unity.Mathematics;

namespace PointCloud.Core.Data
{
    /// <summary>
    /// Metadata for one generic scalar field.
    ///
    /// <see cref="Name"/> is the verbatim source property name — a CV engineer debugging
    /// an export pipeline needs to see "scalar_C2C_absolute_distances", not "Scalar 0".
    /// </summary>
    public sealed class ScalarFieldDescriptor
    {
        /// <summary>Original source property name, unmodified.</summary>
        public string Name;

        /// <summary>Best guess at meaning, inferred from the name. Drives the default colormap.</summary>
        public ScalarSemantic Semantic = ScalarSemantic.Unknown;

        /// <summary>Min/max actually observed while loading. Never mutated afterwards.</summary>
        public float2 SourceRange;

        /// <summary>The range currently mapped to the colormap. User-overridable.</summary>
        public float2 DisplayRange;

        /// <summary>Unit string if the source declared one, else null.</summary>
        public string Unit;

        /// <summary>
        /// True when the source range straddles zero. Signed fields default to a diverging
        /// colormap centred on zero, because for a residual the sign is the interesting part.
        /// </summary>
        public bool IsSigned => SourceRange.x < 0f && SourceRange.y > 0f;

        public override string ToString() =>
            $"{Name} [{SourceRange.x:G6} .. {SourceRange.y:G6}]" +
            (string.IsNullOrEmpty(Unit) ? "" : $" {Unit}") +
            (Semantic == ScalarSemantic.Unknown ? "" : $" ({Semantic})");
    }
}
