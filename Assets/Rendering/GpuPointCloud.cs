using System;
using System.Collections.Generic;
using PointCloud.Core.Data;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace PointCloud.Rendering
{
    /// <summary>
    /// GPU residency for one point cloud: one <see cref="GraphicsBuffer.Target.Raw"/>
    /// buffer per present attribute, laid out chunk-major, plus the indirect draw args.
    ///
    /// Raw (ByteAddressBuffer) for every attribute rather than typed StructuredBuffers:
    /// one binding path, one wrapper, no per-element-type shader variants, and no
    /// stride-alignment questions (a StructuredBuffer&lt;float3&gt; at stride 12 is legal
    /// but drivers vary in how happily they handle it). Raw loads are as fast as structured
    /// loads on every DX11+ GPU.
    /// </summary>
    public sealed class GpuPointCloud : IDisposable
    {
        public static class Props
        {
            public static readonly int Positions   = Shader.PropertyToID("_Positions");
            public static readonly int Colors      = Shader.PropertyToID("_Colors");
            public static readonly int Normals     = Shader.PropertyToID("_Normals");
            public static readonly int ScalarField = Shader.PropertyToID("_ScalarField");
            public static readonly int CloudToWorld = Shader.PropertyToID("_CloudToWorld");
            public static readonly int PointCount  = Shader.PropertyToID("_PointCount");
        }

        readonly Dictionary<PointAttributes, GraphicsBuffer> _buffers = new();
        readonly List<GraphicsBuffer.IndirectDrawArgs> _commandScratch = new(256);

        GraphicsBuffer.IndirectDrawArgs[] _commandUpload = new GraphicsBuffer.IndirectDrawArgs[256];
        bool _disposed;

        public PointCloudDescriptor Descriptor { get; }

        /// <summary>CPU mirror of the chunk table, used by the culler. Owned by the source data, not copied.</summary>
        public NativeArray<PointChunk> Chunks { get; private set; }

        public GraphicsBuffer IndirectArgs { get; private set; }

        /// <summary>Per-cloud material instance. Owns this cloud's buffer bindings.</summary>
        public Material Material { get; private set; }

        public PointCloudDisplaySettings Display { get; } = new();

        /// <summary>
        /// Source-space conversion: up-axis convention and unit scale. Set by the app from
        /// the descriptor; not something the user moves.
        /// </summary>
        public Matrix4x4 BaseTransform { get; set; } = Matrix4x4.identity;

        /// <summary>
        /// User placement on top of <see cref="BaseTransform"/>, in world units.
        ///
        /// Kept separate so re-orienting a cloud (up-axis toggle) and moving it (aligning it
        /// with another cloud) never overwrite each other.
        /// </summary>
        public Vector3 Translation { get; set; } = Vector3.zero;

        /// <summary>Full cloud-to-world transform. This is what the shader receives.</summary>
        public Matrix4x4 CloudToWorld => Matrix4x4.Translate(Translation) * BaseTransform;

        /// <summary>True when the user has moved this cloud off its source position.</summary>
        public bool IsTranslated => Translation != Vector3.zero;

        /// <summary>How many points have finished uploading. The renderer draws only this prefix.</summary>
        public int UploadedPointCount { get; private set; }

        public bool IsFullyUploaded => UploadedPointCount >= Descriptor.PointCount;

        public long VramBytes { get; private set; }

        /// <summary>Number of draw commands written into <see cref="IndirectArgs"/> this frame.</summary>
        public int CommandCount { get; private set; }

        /// <summary>Points actually submitted this frame, after culling and LOD.</summary>
        public int DrawnPointCount { get; private set; }

        /// <summary>World-space bounds. RenderParams.worldBounds must enclose this or Unity culls the draw.</summary>
        public Bounds WorldBounds { get; private set; }

        readonly List<StreamUpload> _uploads = new();

        /// <summary>
        /// A stream mid-upload. Source is a reinterpreted view into the owning
        /// PointCloudData and is NOT owned here — that data must outlive the upload, which
        /// is why callers only apply CpuRetention once IsFullyUploaded is true.
        /// </summary>
        struct StreamUpload
        {
            public PointAttributes   Attribute;
            public NativeArray<uint> Source;
            public GraphicsBuffer    Destination;
            public int               WordsPerPoint;   // 3 for position, 1 for everything else
        }

        public GpuPointCloud(PointCloudData data, Shader shader)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (shader == null) throw new ArgumentNullException(nameof(shader));

            Descriptor = data.Descriptor;
            Chunks = data.Chunks;

            Material = new Material(shader) { name = $"PointCloud_{Descriptor.Name}", hideFlags = HideFlags.HideAndDontSave };

            BaseTransform = Descriptor.SourceToWorld;
            RecomputeWorldBounds();

            AllocateBuffers(data);

            // At most one command per chunk, plus one so a fully-visible cloud with no chunk
            // table (shouldn't happen, but a zero-length args buffer is an obscure crash) fits.
            int maxCommands = Mathf.Max(1, Chunks.IsCreated ? Chunks.Length : 1);
            IndirectArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments,
                                              maxCommands, GraphicsBuffer.IndirectDrawArgs.size);
            VramBytes += (long)maxCommands * GraphicsBuffer.IndirectDrawArgs.size;

            Display.ApplyDefaultsFor(Descriptor);
            Material.SetMatrix(Props.CloudToWorld, CloudToWorld);
            Material.SetInteger(Props.PointCount, Descriptor.PointCount);
        }

        void AllocateBuffers(PointCloudData data)
        {
            long budget = SystemInfo.maxGraphicsBufferSize;

            foreach (var attribute in PointAttributeInfo.Enumerate(Descriptor.Attributes))
            {
                if (!data.TryGet(attribute, out var stream)) continue;

                long bytes = (long)stream.ElementSize * Descriptor.PointCount;
                if (bytes > budget)
                    throw new InvalidOperationException(
                        $"'{Descriptor.Name}' {PointAttributeInfo.Name(attribute)} stream needs " +
                        $"{bytes / (1024 * 1024)} MB but this GPU caps a single buffer at " +
                        $"{budget / (1024 * 1024)} MB. Buffer paging is not implemented yet.");

                // Raw buffers address in 4-byte words. Element sizes are 4 or 12, so this is exact.
                int words = (int)(bytes / 4);
                var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, words, 4)
                {
                    name = $"{Descriptor.Name}_{PointAttributeInfo.Name(attribute)}",
                };

                _buffers[attribute] = buffer;
                VramBytes += bytes;

                _uploads.Add(new StreamUpload
                {
                    Attribute     = attribute,
                    Source        = stream.Data.Reinterpret<uint>(1),
                    Destination   = buffer,
                    WordsPerPoint = stream.ElementSize / 4,
                });

                BindBuffer(attribute, buffer);
            }
        }

        void BindBuffer(PointAttributes attribute, GraphicsBuffer buffer)
        {
            switch (attribute)
            {
                case PointAttributes.Position: Material.SetBuffer(Props.Positions, buffer); break;
                case PointAttributes.Color:    Material.SetBuffer(Props.Colors, buffer); break;
                case PointAttributes.Normal:   Material.SetBuffer(Props.Normals, buffer); break;
            }
        }

        /// <summary>
        /// Point the generic scalar slot at one of the per-point scalar-ish streams. The
        /// shader reinterprets it with asfloat/asuint per mode, so an intensity float buffer
        /// and a label uint buffer share one binding with no shader keyword at all.
        /// </summary>
        public bool BindScalarSlot(PointAttributes attribute)
        {
            if (!_buffers.TryGetValue(attribute, out var buffer)) return false;
            Material.SetBuffer(Props.ScalarField, buffer);
            return true;
        }

        public bool Has(PointAttributes attribute) => _buffers.ContainsKey(attribute);

        /// <summary>
        /// Push up to <paramref name="byteBudget"/> bytes to the GPU. Called once per frame:
        /// a 600 MB cloud lands over ~75 frames with the cloud visibly filling in the whole
        /// time and no hitching, instead of one multi-second stall.
        /// </summary>
        public void AdvanceUpload(long byteBudget = 8L * 1024 * 1024)
        {
            if (_disposed || IsFullyUploaded || _uploads.Count == 0) return;

            // Advance every stream over the SAME point range, rather than filling one stream
            // at a time. The drawable prefix is bounded by the least-advanced stream — a
            // point whose position has landed but whose colour has not would flash garbage —
            // so uploading sequentially would pin the prefix at zero until the first stream
            // finished, and there would be no progressive display at all.
            long bytesPerPoint = Math.Max(1, Descriptor.BytesPerPoint);
            int remaining = Descriptor.PointCount - UploadedPointCount;
            int slice = (int)Math.Min(remaining, Math.Max(1, byteBudget / bytesPerPoint));

            foreach (var upload in _uploads)
            {
                int start = UploadedPointCount * upload.WordsPerPoint;
                int words = slice * upload.WordsPerPoint;
                upload.Destination.SetData(upload.Source, start, start, words);
            }

            UploadedPointCount += slice;
            if (IsFullyUploaded) _uploads.Clear();
        }

        /// <summary>Upload everything now. Used by tests and by clouds small enough that slicing is pointless.</summary>
        public void UploadAll() => AdvanceUpload(long.MaxValue);

        public float UploadProgress =>
            Descriptor.PointCount <= 0 ? 1f : Mathf.Clamp01((float)UploadedPointCount / Descriptor.PointCount);

        /// <summary>
        /// Write the frame's draw commands. Each command covers a run of points; because a
        /// non-indexed D3D draw includes StartVertexLocation in SV_VertexID, startVertex
        /// alone offsets the point id — so all runs share one material with no property
        /// block and one C# call.
        /// </summary>
        public void SetDrawCommands(List<GraphicsBuffer.IndirectDrawArgs> commands)
        {
            CommandCount = 0;
            DrawnPointCount = 0;
            if (commands.Count == 0) return;

            if (_commandUpload.Length < commands.Count)
                _commandUpload = new GraphicsBuffer.IndirectDrawArgs[Mathf.NextPowerOfTwo(commands.Count)];

            for (int i = 0; i < commands.Count; i++)
            {
                _commandUpload[i] = commands[i];
                DrawnPointCount += (int)(commands[i].vertexCountPerInstance / 6);
            }

            IndirectArgs.SetData(_commandUpload, 0, 0, commands.Count);
            CommandCount = commands.Count;
        }

        /// <summary>M1 path: draw the whole uploaded prefix as one command. Culling replaces this in M5.</summary>
        public void SetSingleDrawCommand()
        {
            _commandScratch.Clear();
            if (UploadedPointCount > 0)
            {
                _commandScratch.Add(new GraphicsBuffer.IndirectDrawArgs
                {
                    vertexCountPerInstance = (uint)UploadedPointCount * 6u,
                    instanceCount          = 1u,
                    startVertex            = 0u,
                    startInstance          = 0u,
                });
            }
            SetDrawCommands(_commandScratch);
        }

        /// <summary>
        /// Move this cloud so its centre sits on the world origin.
        ///
        /// The point of it: two clouds captured in different coordinate frames — a scan and
        /// a prediction, or the same scene from two sessions — land kilometres apart and
        /// cannot be visually compared at all. Zeroing each one brings them into the same
        /// place so they overlay. It is a display transform only; nothing is written back
        /// and <see cref="ResetTransform"/> restores the source position exactly.
        /// </summary>
        public void CenterAtOrigin()
        {
            // Centre in world space BEFORE translation, so calling this twice is idempotent
            // rather than drifting further each press.
            var center = BaseTransform.MultiplyPoint3x4(Descriptor.LocalBounds.center);
            Translation = -center;
            ApplyTransform();
        }

        /// <summary>Restore the cloud's source position.</summary>
        public void ResetTransform()
        {
            Translation = Vector3.zero;
            ApplyTransform();
        }

        /// <summary>Push the current transform to the material and refresh the culling bounds.</summary>
        public void ApplyTransform()
        {
            if (Material != null) Material.SetMatrix(Props.CloudToWorld, CloudToWorld);
            RecomputeWorldBounds();
        }

        /// <summary>
        /// Nearest chunk-AABB hit along a world-space ray, or false on a miss.
        ///
        /// Chunk granularity rather than whole-cloud bounds: the chunk table is already
        /// resident for culling, and 16-160 tight boxes approximate the actual surface far
        /// better than one box around everything. That matters for zoom-to-cursor, where
        /// the whole-cloud box can be tens of metres in front of the geometry the user is
        /// actually looking at.
        /// </summary>
        public bool TryRaycastChunks(Ray worldRay, out float distance)
        {
            distance = float.PositiveInfinity;
            if (!Chunks.IsCreated || Chunks.Length == 0) return false;

            // Transform the ray into cloud space once, rather than transforming every box.
            var inverse = CloudToWorld.inverse;
            Vector3 origin    = inverse.MultiplyPoint3x4(worldRay.origin);
            Vector3 direction = inverse.MultiplyVector(worldRay.direction);
            if (direction.sqrMagnitude < 1e-20f) return false;

            float best = float.PositiveInfinity;

            for (int i = 0; i < Chunks.Length; i++)
            {
                var chunk = Chunks[i];
                if (SlabIntersect(origin, direction, chunk.BoundsMin, chunk.BoundsMax, out float t) && t < best)
                    best = t;
            }

            if (float.IsPositiveInfinity(best)) return false;

            // Convert back through the transform so any scale in it is respected.
            Vector3 localHit = origin + direction * best;
            distance = Vector3.Distance(worldRay.origin, CloudToWorld.MultiplyPoint3x4(localHit));
            return true;
        }

        /// <summary>Standard slab test. Returns the entry distance, or 0 when the origin is inside.</summary>
        static bool SlabIntersect(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float t)
        {
            float tMin = 0f;
            float tMax = float.PositiveInfinity;

            for (int axis = 0; axis < 3; axis++)
            {
                float d = direction[axis];
                float o = origin[axis];

                if (Mathf.Abs(d) < 1e-12f)
                {
                    // Parallel to this slab: a miss unless the origin already lies within it.
                    if (o < min[axis] || o > max[axis]) { t = 0f; return false; }
                    continue;
                }

                float inverseD = 1f / d;
                float t1 = (min[axis] - o) * inverseD;
                float t2 = (max[axis] - o) * inverseD;
                if (t1 > t2) (t1, t2) = (t2, t1);

                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMin > tMax) { t = 0f; return false; }
            }

            t = tMin;
            return true;
        }

        public void RecomputeWorldBounds()
        {
            var local = Descriptor.LocalBounds;
            var center = CloudToWorld.MultiplyPoint3x4(local.center);

            // Transform the extents by the absolute matrix so a rotated cloud still gets a
            // bound that encloses it — RenderParams.worldBounds is a hard cull, not a hint.
            var e = local.extents;
            var m = CloudToWorld;
            var extents = new Vector3(
                Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);

            WorldBounds = new Bounds(center, extents * 2f);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var buffer in _buffers.Values) buffer?.Dispose();
            _buffers.Clear();
            _uploads.Clear();

            IndirectArgs?.Dispose();
            IndirectArgs = null;

            if (Material != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(Material);
                else UnityEngine.Object.DestroyImmediate(Material);
                Material = null;
            }

            Chunks = default;   // not owned
            VramBytes = 0;
        }
    }
}
