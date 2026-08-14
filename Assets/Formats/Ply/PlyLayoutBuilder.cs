using System;
using System.Collections.Generic;
using PointCloud.Core.Data;
using PointCloud.Core.Diagnostics;

namespace PointCloud.Formats.Ply
{
    /// <summary>Result of planning a decode: what to read, and what the caller should be told.</summary>
    public sealed class PlyLayoutPlan
    {
        public PlyDecodeLayout Layout;
        public PointAttributes Attributes;
        public ScalarFieldDescriptor[] ScalarFields = Array.Empty<ScalarFieldDescriptor>();
        public bool PositionsAreDouble;
        public int TokenCount;                    // ASCII only
        public readonly List<string> Notes = new();
    }

    /// <summary>
    /// Turns a parsed vertex element into a concrete decode plan.
    ///
    /// Two decisions here matter more than the rest. Colour type drives whether values are
    /// 0-255 sRGB bytes or 0-1 linear floats, and getting it wrong washes out every
    /// float-coloured file. Double positions signal a geo-referenced cloud, which needs an
    /// origin offset or float32 quantisation turns a survey into a wobbling mess.
    /// </summary>
    public static class PlyLayoutBuilder
    {
        public const int MaxScalarFields = 4;

        public static PlyLayoutPlan Build(PlyElement vertex, PlyFormat format, PointAttributes wanted)
        {
            if (vertex == null)
                throw new PointCloudFormatException("ply", "File has no 'vertex' element.");

            var plan = new PlyLayoutPlan();
            plan.Layout.BigEndian = format == PlyFormat.BinaryBigEndian;
            bool ascii = format == PlyFormat.Ascii;

            var scalarFields = new List<ScalarFieldDescriptor>();
            int offset = 0;

            for (int i = 0; i < vertex.Properties.Count; i++)
            {
                var property = vertex.Properties[i];

                if (property.IsList)
                    throw new PointCloudFormatException("ply",
                        $"Vertex property '{property.Name}' is a list, which makes records " +
                        "variable-length. Per-vertex list properties are not supported.");

                // ASCII addresses by token index, binary by byte offset.
                var slot = new PlySlot { Offset = ascii ? i : offset, Type = property.Type };
                offset += PlyScalar.Size(property.Type);

                var semantic = PlySemanticMap.Resolve(property.Name);
                if (semantic.IsMapped)
                {
                    Assign(plan, semantic, slot, property);
                    continue;
                }

                if (scalarFields.Count >= MaxScalarFields)
                {
                    plan.Notes.Add(
                        $"property '{property.Name}' ignored — only {MaxScalarFields} generic scalar " +
                        "fields are supported and they are already taken");
                    continue;
                }

                int scalarIndex = scalarFields.Count;
                AssignScalar(plan, scalarIndex, slot);
                scalarFields.Add(new ScalarFieldDescriptor
                {
                    Name     = property.Name,
                    Semantic = PlySemanticMap.GuessSemantic(property.Name),
                });

                plan.Notes.Add($"property '{property.Name}' → scalar field {scalarIndex}");
            }

            plan.Layout.Stride = ascii ? 0 : offset;
            plan.TokenCount    = vertex.Properties.Count;

            if (!plan.Layout.HasPosition)
                throw new PointCloudFormatException("ply",
                    "Vertex element has no x/y/z properties, so there are no points to show.");

            // Only the presence of red/green/blue makes a cloud coloured; a stray 'alpha'
            // on its own does not.
            var attributes = PointAttributes.Position;
            if (plan.Layout.HasColor)  attributes |= PointAttributes.Color;
            if (plan.Layout.HasNormal) attributes |= PointAttributes.Normal;
            if (plan.Layout.Intensity.IsValid)  attributes |= PointAttributes.Intensity;
            if (plan.Layout.Label.IsValid)      attributes |= PointAttributes.Label;
            if (plan.Layout.Confidence.IsValid) attributes |= PointAttributes.Confidence;
            if (plan.Layout.Timestamp.IsValid)  attributes |= PointAttributes.Timestamp;

            for (int i = 0; i < scalarFields.Count; i++)
                attributes |= PointAttributeInfo.ScalarSlot(i);

            // Position is never optional, whatever the caller asked for.
            plan.Attributes = (attributes & wanted) | PointAttributes.Position;
            plan.ScalarFields = scalarFields.ToArray();

            plan.PositionsAreDouble = plan.Layout.X.Type == PlyScalarType.Float64 ||
                                      plan.Layout.Y.Type == PlyScalarType.Float64 ||
                                      plan.Layout.Z.Type == PlyScalarType.Float64;

            if (plan.Layout.HasColor)
            {
                plan.Layout.ColorIsFloat = plan.Layout.R.Type is PlyScalarType.Float32 or PlyScalarType.Float64;
                plan.Notes.Add(plan.Layout.ColorIsFloat
                    ? "colour is float (0-1, treated as linear)"
                    : "colour is integer (0-255, treated as sRGB)");
            }

            if (plan.PositionsAreDouble)
                plan.Notes.Add("positions are double — an origin offset will be applied to preserve precision");

            return plan;
        }

        static void Assign(PlyLayoutPlan plan, in PlySemantic semantic, in PlySlot slot, PlyProperty property)
        {
            switch (semantic.Attribute)
            {
                case PointAttributes.Position:
                    if (semantic.Component == 0) plan.Layout.X = slot;
                    else if (semantic.Component == 1) plan.Layout.Y = slot;
                    else plan.Layout.Z = slot;
                    break;

                case PointAttributes.Color:
                    if (semantic.Component == 0) plan.Layout.R = slot;
                    else if (semantic.Component == 1) plan.Layout.G = slot;
                    else if (semantic.Component == 2) plan.Layout.B = slot;
                    else plan.Layout.A = slot;
                    break;

                case PointAttributes.Normal:
                    if (semantic.Component == 0) plan.Layout.NX = slot;
                    else if (semantic.Component == 1) plan.Layout.NY = slot;
                    else plan.Layout.NZ = slot;
                    break;

                case PointAttributes.Intensity:  plan.Layout.Intensity = slot; break;
                case PointAttributes.Label:      plan.Layout.Label = slot; break;
                case PointAttributes.Confidence: plan.Layout.Confidence = slot; break;
                case PointAttributes.Timestamp:  plan.Layout.Timestamp = slot; break;

                default:
                    plan.Notes.Add($"property '{property.Name}' mapped to an unhandled attribute; ignored");
                    break;
            }
        }

        static void AssignScalar(PlyLayoutPlan plan, int index, in PlySlot slot)
        {
            switch (index)
            {
                case 0: plan.Layout.Scalar0 = slot; break;
                case 1: plan.Layout.Scalar1 = slot; break;
                case 2: plan.Layout.Scalar2 = slot; break;
                default: plan.Layout.Scalar3 = slot; break;
            }
        }
    }
}
