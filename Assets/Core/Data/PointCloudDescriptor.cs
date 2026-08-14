using System;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace PointCloud.Core.Data
{
    /// <summary>
    /// Everything known about a cloud except the points themselves. Small, managed, and
    /// immutable in practice after loading — safe to copy, log, and bind to UI.
    /// </summary>
    public sealed class PointCloudDescriptor
    {
        /// <summary>Display name. Defaults to the file name.</summary>
        public string Name = "unnamed";

        /// <summary>Absolute source path, or a synthetic descriptor like "synthetic://sphere".</summary>
        public string SourcePath;

        /// <summary>Format id of the parser that produced this: "ply", "pcd", "obj", "fbx", "synthetic", "vrs".</summary>
        public string FormatId = "unknown";

        public int PointCount;

        public PointAttributes Attributes = PointAttributes.Position;

        public PositionEncoding PositionEncoding = PositionEncoding.Float32;

        /// <summary>
        /// Positions are stored RELATIVE to this. The absolute source coordinate of point i
        /// is <c>OriginOffset + (double3)positions[i]</c>.
        ///
        /// This is what keeps geo-referenced clouds usable: UTM eastings near 5e5 have
        /// 0.25-0.5 m of float32 quantisation, which renders as a visibly wobbling mess.
        /// Storing relative keeps world coordinates near zero so float32 view math is exact,
        /// and the inspector can still report the exact absolute coordinate in double.
        /// </summary>
        public double3 OriginOffset;

        /// <summary>AABB of the stored (relative) positions.</summary>
        public Bounds LocalBounds;

        /// <summary>
        /// Source-space to world-space: up-axis convention and unit scale. Z-up right-handed
        /// metres is the default for sensor data; FBX overrides it from its own GlobalSettings.
        /// </summary>
        public Matrix4x4 SourceToWorld = Matrix4x4.identity;

        /// <summary>
        /// True when the format itself states its orientation (FBX GlobalSettings, or a
        /// generator that authored directly in world space). The global up-axis toggle skips
        /// these, because guessing over authoritative metadata is how a correctly-oriented
        /// cloud ends up on its side.
        /// </summary>
        public bool OrientationIsAuthoritative;

        /// <summary>
        /// False when per-point colours are sRGB-encoded bytes (PLY/PCD uchar, OBJ) — the
        /// common case. True when they are already linear floats. The shader converts based
        /// on this; getting it wrong washes out every float-coloured cloud.
        /// </summary>
        public bool ColorIsLinear;

        /// <summary>Metadata for Scalar0..3, indexed to match <see cref="PointAttributeInfo.ScalarSlot"/>.</summary>
        public ScalarFieldDescriptor[] ScalarFields = Array.Empty<ScalarFieldDescriptor>();

        /// <summary>Distinct label values seen at load, for the legend. Null when unknown or unbounded.</summary>
        public uint[] LabelValues;

        /// <summary>0 for static clouds.</summary>
        public int FrameIndex;

        /// <summary>Frame timestamp in seconds. Timestamp attribute values are microseconds from here.</summary>
        public double FrameTimeSeconds;

        /// <summary>Sensor pose if the source supplied one (PCD VIEWPOINT, VRS per-frame pose), else identity.</summary>
        public Pose SensorPose = Pose.identity;

        /// <summary>
        /// Median nearest-neighbour spacing, estimated from a sample at load. Seeds the
        /// default point radius — the difference between a cloud that opens looking right
        /// and one that opens looking like TV static.
        /// </summary>
        public float MedianPointSpacing = -1f;

        /// <summary>Points the source contained but we dropped (NaN, invalid returns).</summary>
        public int DroppedPointCount;

        public bool Has(PointAttributes attributes) => (Attributes & attributes) == attributes;

        public long BytesPerPoint => PointAttributeInfo.BytesPerPoint(Attributes);

        public long EstimatedBytes => BytesPerPoint * PointCount;

        /// <summary>Absolute source coordinate of a stored relative position.</summary>
        public double3 ToAbsolute(float3 relativePosition) => OriginOffset + (double3)relativePosition;

        public ScalarFieldDescriptor ScalarField(int index) =>
            ScalarFields != null && index >= 0 && index < ScalarFields.Length ? ScalarFields[index] : null;

        public override string ToString()
        {
            var attrs = string.Join(", ", PointAttributeInfo.Enumerate(Attributes).Select(PointAttributeInfo.Name));
            return $"{Name} ({FormatId}) — {PointCount:N0} points, {BytesPerPoint} B/pt, [{attrs}]";
        }
    }
}
