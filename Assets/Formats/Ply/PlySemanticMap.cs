using System;
using PointCloud.Core.Data;

namespace PointCloud.Formats.Ply
{
    /// <summary>Which attribute and component a source property feeds.</summary>
    public readonly struct PlySemantic
    {
        public readonly PointAttributes Attribute;
        /// <summary>0-2 for xyz/rgb/normals, 3 for alpha, 0 for single-valued attributes.</summary>
        public readonly int Component;

        public PlySemantic(PointAttributes attribute, int component = 0)
        {
            Attribute = attribute;
            Component = component;
        }

        public static readonly PlySemantic None = new(PointAttributes.None);
        public bool IsMapped => Attribute != PointAttributes.None;
    }

    /// <summary>
    /// Maps PLY property names onto attributes.
    ///
    /// Matching is case-insensitive and strips the "scalar_" prefix that CloudCompare puts
    /// on everything it exports, so a round-trip through CloudCompare does not turn
    /// intensity into an anonymous scalar field. Anything unrecognised is kept as a named
    /// scalar rather than dropped — an engineer's custom per-point value is usually the
    /// exact thing they opened the tool to look at.
    /// </summary>
    public static class PlySemanticMap
    {
        public static PlySemantic Resolve(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return PlySemantic.None;

            var name = Normalize(rawName);

            switch (name)
            {
                case "x": return new PlySemantic(PointAttributes.Position, 0);
                case "y": return new PlySemantic(PointAttributes.Position, 1);
                case "z": return new PlySemantic(PointAttributes.Position, 2);

                case "nx" or "normal_x" or "normalx": return new PlySemantic(PointAttributes.Normal, 0);
                case "ny" or "normal_y" or "normaly": return new PlySemantic(PointAttributes.Normal, 1);
                case "nz" or "normal_z" or "normalz": return new PlySemantic(PointAttributes.Normal, 2);

                case "red" or "r" or "diffuse_red":     return new PlySemantic(PointAttributes.Color, 0);
                case "green" or "g" or "diffuse_green": return new PlySemantic(PointAttributes.Color, 1);
                case "blue" or "b" or "diffuse_blue":   return new PlySemantic(PointAttributes.Color, 2);
                case "alpha" or "a" or "opacity":       return new PlySemantic(PointAttributes.Color, 3);

                case "intensity" or "reflectance" or "scalar_intensity" or "i":
                    return new PlySemantic(PointAttributes.Intensity);

                case "label" or "class" or "classification" or "segment_id" or "seg_id" or "category":
                    return new PlySemantic(PointAttributes.Label);

                case "confidence" or "quality" or "score" or "probability":
                    return new PlySemantic(PointAttributes.Confidence);

                case "gps_time" or "timestamp" or "time" or "t":
                    return new PlySemantic(PointAttributes.Timestamp);

                default:
                    return PlySemantic.None;   // caller assigns it a generic scalar slot
            }
        }

        /// <summary>Lower-case, trimmed, and with CloudCompare's "scalar_" prefix removed.</summary>
        public static string Normalize(string rawName)
        {
            var name = rawName.Trim().ToLowerInvariant();
            const string prefix = "scalar_";
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : name;
        }

        /// <summary>
        /// Best guess at what an unrecognised scalar means, from its name. Only drives the
        /// default colormap — a signed residual wants a diverging ramp, a distance does not.
        /// </summary>
        public static ScalarSemantic GuessSemantic(string rawName)
        {
            var name = Normalize(rawName);

            // Order matters, most specific first. CloudCompare writes
            // "C2C_absolute_distances", which contains both "c2c" and "dist" — checking the
            // generic token first would classify every cloud-to-cloud comparison as a plain
            // distance and default it to a sequential ramp, losing the sign that is the
            // entire point of a deviation.
            if (Contains(name, "deviation") || Contains(name, "c2c") || Contains(name, "c2m"))
                return ScalarSemantic.Deviation;
            if (Contains(name, "residual") || Contains(name, "error") || Contains(name, "err"))
                return ScalarSemantic.Residual;
            if (Contains(name, "curv")) return ScalarSemantic.Curvature;
            if (Contains(name, "dist")) return ScalarSemantic.Distance;
            if (Contains(name, "prob") || Contains(name, "conf")) return ScalarSemantic.Probability;
            if (Contains(name, "range") || Contains(name, "depth")) return ScalarSemantic.Range;
            if (Contains(name, "angle") || Contains(name, "incidence")) return ScalarSemantic.Angle;
            if (Contains(name, "count") || Contains(name, "num") || Contains(name, "return"))
                return ScalarSemantic.Count;

            return ScalarSemantic.Unknown;
        }

        static bool Contains(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.Ordinal) >= 0;
    }
}
