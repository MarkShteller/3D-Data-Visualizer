using System.Collections.Generic;
using PointCloud.Core.Data;
using PointCloud.Core.Encoding;
using PointCloud.Core.Spatial;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace PointCloud.Core.Synthetic
{
    public enum SyntheticShape
    {
        /// <summary>Unit sphere surface. Every point is at exactly radius = Scale.</summary>
        SphereShell,
        /// <summary>Flat XY plane. Degenerate on one axis — exercises the flat-cloud paths.</summary>
        PlaneGrid,
        Torus,
        /// <summary>Solid gaussian cloud. Wildly varying density — the interesting LOD case.</summary>
        GaussianBlob,
        /// <summary>Thin helix. Almost all chunks empty of screen area — good culling test.</summary>
        Helix,
        /// <summary>Value-noise heightfield. The closest synthetic analogue to a real scan.</summary>
        Terrain,
    }

    public struct SyntheticCloudSettings
    {
        public SyntheticShape  Shape;
        public int             PointCount;
        public PointAttributes Attributes;
        public uint            Seed;
        public float           Scale;
        /// <summary>Number of distinct label values, when Label is requested.</summary>
        public int             LabelCount;

        public static SyntheticCloudSettings Default(SyntheticShape shape = SyntheticShape.SphereShell,
                                                     int pointCount = 1_000_000) => new()
        {
            Shape      = shape,
            PointCount = pointCount,
            Attributes = PointAttributes.Position | PointAttributes.Color | PointAttributes.Normal |
                         PointAttributes.Intensity | PointAttributes.Label | PointAttributes.Confidence |
                         PointAttributes.Scalar0,
            Seed       = 12345u,
            Scale      = 10f,
            LabelCount = 14,
        };
    }

    /// <summary>
    /// Generates point clouds procedurally.
    ///
    /// Not a test helper — it ships in the UI. It unblocks the renderer before any parser
    /// exists, gives a reproducible 20M-point performance case that does not depend on
    /// anyone's file, and produces analytically checkable data: on a SphereShell every
    /// point is at radius exactly Scale, so a "distance from origin" scalar must read
    /// Scale +/- 1e-6 and the AABB must be exactly +/-Scale.
    ///
    /// Every attribute is a pure function of position, deliberately. ChunkBuilder reorders
    /// points, so any index-derived value would be scrambled and untestable afterwards.
    /// </summary>
    public static class SyntheticCloudGenerator
    {
        public static PointCloudData Generate(SyntheticCloudSettings settings,
                                              Allocator allocator = Allocator.Persistent,
                                              bool buildChunks = true)
        {
            int count = math.max(1, settings.PointCount);
            float scale = settings.Scale <= 0f ? 1f : settings.Scale;
            int labelCount = math.max(1, settings.LabelCount);

            // Position is not optional.
            var attributes = settings.Attributes | PointAttributes.Position;

            var descriptor = new PointCloudDescriptor
            {
                Name       = $"{settings.Shape} {FormatCount(count)}",
                SourcePath = $"synthetic://{settings.Shape.ToString().ToLowerInvariant()}",
                FormatId   = "synthetic",
                PointCount = count,
                Attributes = PointAttributes.Position,
                // Generated colours are authored as linear values, unlike PLY/PCD byte colour.
                ColorIsLinear   = true,
                // Generated directly in Unity's Y-up world space, so the global up-axis
                // toggle must leave these alone.
                OrientationIsAuthoritative = true,
                FrameTimeSeconds = 0.0,
            };

            var data = new PointCloudData(descriptor);

            var positions = data.AddStream(PointAttributes.Position, allocator).As<float3>();

            // The job safety system requires every NativeArray field to be constructed, even
            // on a branch the job never takes. Absent attributes therefore get a one-element
            // placeholder that the job never writes to.
            var placeholders = new List<NativeArray<uint>>(7);

            var colors      = TryAdd(data, attributes, PointAttributes.Color, allocator, placeholders);
            var normals     = TryAdd(data, attributes, PointAttributes.Normal, allocator, placeholders);
            var intensities = TryAdd(data, attributes, PointAttributes.Intensity, allocator, placeholders);
            var labels      = TryAdd(data, attributes, PointAttributes.Label, allocator, placeholders);
            var confidences = TryAdd(data, attributes, PointAttributes.Confidence, allocator, placeholders);
            var timestamps  = TryAdd(data, attributes, PointAttributes.Timestamp, allocator, placeholders);
            var scalar0     = TryAdd(data, attributes, PointAttributes.Scalar0, allocator, placeholders);

            new GenerateJob
            {
                Shape       = settings.Shape,
                Seed        = settings.Seed == 0u ? 1u : settings.Seed,
                Scale       = scale,
                LabelCount  = labelCount,
                Mask        = attributes,
                Positions   = positions,
                Colors      = colors,
                Normals     = normals,
                Intensities = intensities,
                Labels      = labels,
                Confidences = confidences,
                Timestamps  = timestamps,
                Scalar0     = scalar0,
            }.Schedule(count, 8192).Complete();

            foreach (var placeholder in placeholders) placeholder.Dispose();

            if (data.Descriptor.Has(PointAttributes.Scalar0))
            {
                data.Descriptor.ScalarFields = new[]
                {
                    new ScalarFieldDescriptor
                    {
                        Name         = "distance_from_origin",
                        Semantic     = ScalarSemantic.Distance,
                        SourceRange  = new float2(0f, scale * 1.5f),
                        DisplayRange = new float2(0f, scale * 1.5f),
                        Unit         = "m",
                    },
                };
            }

            if (data.Descriptor.Has(PointAttributes.Label))
            {
                var values = new uint[labelCount];
                for (int i = 0; i < labelCount; i++) values[i] = (uint)i;
                data.Descriptor.LabelValues = values;
            }

            // A rough spacing estimate: nth root of volume per point. Good enough to seed the
            // default point radius, and exact enough that a sphere shell opens looking solid.
            data.Descriptor.MedianPointSpacing = EstimateSpacing(settings.Shape, scale, count);

            if (buildChunks)
                ChunkBuilder.Build(data, ChunkBuilder.DefaultChunkSize, settings.Seed, allocator);

            return data;
        }

        static NativeArray<uint> TryAdd(PointCloudData data, PointAttributes mask,
                                        PointAttributes attribute, Allocator allocator,
                                        List<NativeArray<uint>> placeholders)
        {
            if ((mask & attribute) == 0)
            {
                var placeholder = new NativeArray<uint>(1, Allocator.TempJob);
                placeholders.Add(placeholder);
                return placeholder;
            }

            // Every non-position attribute is 4 bytes; uint is the common transport and the
            // job reinterprets where it needs float semantics.
            return data.AddStream(attribute, allocator).As<uint>();
        }

        static float EstimateSpacing(SyntheticShape shape, float scale, int count)
        {
            float area;
            switch (shape)
            {
                case SyntheticShape.SphereShell: area = 4f * math.PI * scale * scale; break;
                case SyntheticShape.Torus:       area = 4f * math.PI * math.PI * scale * (scale * 0.35f); break;
                case SyntheticShape.PlaneGrid:
                case SyntheticShape.Terrain:     area = 4f * scale * scale; break;
                case SyntheticShape.Helix:       area = 2f * math.PI * scale * 4f * (scale * 0.05f); break;
                default:
                    // Volumetric: spacing is the cube root of volume per point.
                    return math.pow(8f * scale * scale * scale / math.max(1, count), 1f / 3f);
            }
            return math.sqrt(area / math.max(1, count));
        }

        static string FormatCount(int n) =>
            n >= 1_000_000 ? $"{n / 1_000_000f:0.#}M" : n >= 1000 ? $"{n / 1000f:0.#}K" : n.ToString();

        [BurstCompile(CompileSynchronously = true)]
        struct GenerateJob : IJobParallelFor
        {
            public SyntheticShape  Shape;
            public uint            Seed;
            public float           Scale;
            public int             LabelCount;
            public PointAttributes Mask;

            [WriteOnly] public NativeArray<float3> Positions;
            [WriteOnly] public NativeArray<uint>   Colors;
            [WriteOnly] public NativeArray<uint>   Normals;
            [WriteOnly] public NativeArray<uint>   Intensities;
            [WriteOnly] public NativeArray<uint>   Labels;
            [WriteOnly] public NativeArray<uint>   Confidences;
            [WriteOnly] public NativeArray<uint>   Timestamps;
            [WriteOnly] public NativeArray<uint>   Scalar0;

            public void Execute(int i)
            {
                var rng = new Random(math.hash(new uint2(Seed, (uint)i + 1u)) | 1u);

                float3 p, n;
                Shapes.Sample(Shape, ref rng, Scale, out p, out n);

                Positions[i] = p;

                if ((Mask & PointAttributes.Normal) != 0)
                    Normals[i] = OctNormal.Encode(n);

                if ((Mask & PointAttributes.Color) != 0)
                {
                    // Position-derived so it survives the chunk reorder and is checkable.
                    float3 rgb = math.saturate(p / (Scale * 2f) + 0.5f);
                    Colors[i] = ColorPack.FromFloat3(rgb);
                }

                if ((Mask & PointAttributes.Intensity) != 0)
                {
                    float intensity = math.saturate(0.5f + 0.5f * math.sin(p.x * 1.7f) * math.cos(p.z * 1.3f));
                    Intensities[i] = math.asuint(intensity);
                }

                if ((Mask & PointAttributes.Label) != 0)
                {
                    // Contiguous spatial bands, which is what a segmentation output looks like
                    // and makes a wrong categorical palette obvious at a glance.
                    float t = math.saturate((math.atan2(p.z, p.x) + math.PI) / (2f * math.PI));
                    Labels[i] = (uint)math.min((int)(t * LabelCount), LabelCount - 1);
                }

                if ((Mask & PointAttributes.Confidence) != 0)
                {
                    float confidence = math.saturate(1f - math.length(p.xz) / (Scale * 1.5f));
                    Confidences[i] = math.asuint(confidence);
                }

                if ((Mask & PointAttributes.Timestamp) != 0)
                    Timestamps[i] = (uint)math.max(0, (int)(math.saturate(p.y / Scale * 0.5f + 0.5f) * 1e6f));

                if ((Mask & PointAttributes.Scalar0) != 0)
                    Scalar0[i] = math.asuint(math.length(p));
            }
        }

        [BurstCompile]
        static class Shapes
        {
            public static void Sample(SyntheticShape shape, ref Random rng, float scale,
                                      out float3 position, out float3 normal)
            {
                switch (shape)
                {
                    case SyntheticShape.PlaneGrid:   PlaneGrid(ref rng, scale, out position, out normal); return;
                    case SyntheticShape.Torus:       Torus(ref rng, scale, out position, out normal); return;
                    case SyntheticShape.GaussianBlob: GaussianBlob(ref rng, scale, out position, out normal); return;
                    case SyntheticShape.Helix:       Helix(ref rng, scale, out position, out normal); return;
                    case SyntheticShape.Terrain:     Terrain(ref rng, scale, out position, out normal); return;
                    default:                         SphereShell(ref rng, scale, out position, out normal); return;
                }
            }

            /// <summary>
            /// Uniform on the sphere via the inverse-CDF method — cos(theta) uniform in
            /// [-1,1]. Sampling theta uniformly instead would cluster at the poles, which
            /// would quietly invalidate any density test built on this shape.
            /// </summary>
            static void SphereShell(ref Random rng, float scale, out float3 p, out float3 n)
            {
                float z   = rng.NextFloat(-1f, 1f);
                float phi = rng.NextFloat(0f, 2f * math.PI);
                float r   = math.sqrt(math.max(0f, 1f - z * z));
                n = new float3(r * math.cos(phi), r * math.sin(phi), z);
                p = n * scale;
            }

            static void PlaneGrid(ref Random rng, float scale, out float3 p, out float3 n)
            {
                p = new float3(rng.NextFloat(-scale, scale), 0f, rng.NextFloat(-scale, scale));
                n = new float3(0f, 1f, 0f);
            }

            static void Torus(ref Random rng, float scale, out float3 p, out float3 n)
            {
                float major = scale;
                float minor = scale * 0.35f;
                float u = rng.NextFloat(0f, 2f * math.PI);
                float v = rng.NextFloat(0f, 2f * math.PI);

                math.sincos(u, out float su, out float cu);
                math.sincos(v, out float sv, out float cv);

                p = new float3((major + minor * cv) * cu, minor * sv, (major + minor * cv) * su);
                n = new float3(cv * cu, sv, cv * su);
            }

            static void GaussianBlob(ref Random rng, float scale, out float3 p, out float3 n)
            {
                float3 g = new float3(Gaussian(ref rng), Gaussian(ref rng), Gaussian(ref rng));
                p = g * (scale * 0.35f);
                n = math.normalizesafe(g, new float3(0f, 1f, 0f));
            }

            static void Helix(ref Random rng, float scale, out float3 p, out float3 n)
            {
                float turns = 4f;
                float t = rng.NextFloat(0f, 1f);
                float a = t * turns * 2f * math.PI;
                math.sincos(a, out float sa, out float ca);

                float3 axis = new float3(ca * scale, (t - 0.5f) * scale * 2f, sa * scale);
                float3 jitter = new float3(Gaussian(ref rng), Gaussian(ref rng), Gaussian(ref rng)) * (scale * 0.02f);

                p = axis + jitter;
                n = math.normalizesafe(new float3(ca, 0f, sa), new float3(0f, 1f, 0f));
            }

            static void Terrain(ref Random rng, float scale, out float3 p, out float3 n)
            {
                float2 xz = new float2(rng.NextFloat(-scale, scale), rng.NextFloat(-scale, scale));

                // Three octaves of gradient noise, and the analytic-ish normal from finite
                // differences of the same function.
                float h = Height(xz, scale);
                const float eps = 0.01f;
                float hx = Height(xz + new float2(eps, 0f), scale);
                float hz = Height(xz + new float2(0f, eps), scale);

                p = new float3(xz.x, h, xz.y);
                n = math.normalize(new float3(-(hx - h) / eps, 1f, -(hz - h) / eps));
            }

            static float Height(float2 xz, float scale)
            {
                float2 q = xz / scale;
                return (noise.snoise(q * 1.3f) * 0.5f +
                        noise.snoise(q * 3.1f) * 0.25f +
                        noise.snoise(q * 7.7f) * 0.125f) * scale * 0.25f;
            }

            /// <summary>Box-Muller, one of the two outputs.</summary>
            static float Gaussian(ref Random rng)
            {
                float u1 = math.max(rng.NextFloat(), 1e-7f);
                float u2 = rng.NextFloat();
                return math.sqrt(-2f * math.log(u1)) * math.cos(2f * math.PI * u2);
            }
        }
    }
}
