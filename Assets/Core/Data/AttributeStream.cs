using System;
using Unity.Collections;

namespace PointCloud.Core.Data
{
    /// <summary>
    /// One CPU-side attribute stream: a flat byte array holding PointCount elements of
    /// this attribute's fixed element size.
    ///
    /// Bytes rather than a typed NativeArray so every attribute has one storage type and
    /// one upload path regardless of whether it holds float3, uint32 or float. Callers
    /// reinterpret at the point of use.
    /// </summary>
    public struct AttributeStream : IDisposable
    {
        public PointAttributes Attribute;

        /// <summary>Bytes per element: 12 for Position, 4 for everything else.</summary>
        public int ElementSize;

        public NativeArray<byte> Data;

        public bool IsCreated => Data.IsCreated;

        public int Count => ElementSize > 0 && Data.IsCreated ? Data.Length / ElementSize : 0;

        public static AttributeStream Allocate(PointAttributes attribute, int pointCount, Allocator allocator)
        {
            int elementSize = PointAttributeInfo.ElementSize(attribute);
            return new AttributeStream
            {
                Attribute   = attribute,
                ElementSize = elementSize,
                // Uninitialised: every byte is written by the parser or generator before use.
                Data = new NativeArray<byte>(elementSize * pointCount, allocator,
                                             NativeArrayOptions.UninitializedMemory),
            };
        }

        /// <summary>Typed view over the raw bytes. T's size must match <see cref="ElementSize"/>.</summary>
        public NativeArray<T> As<T>() where T : struct
        {
            int size = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<T>();
            if (size != ElementSize)
                throw new InvalidOperationException(
                    $"Cannot view {PointAttributeInfo.Name(Attribute)} stream " +
                    $"(element size {ElementSize}) as {typeof(T).Name} (size {size}).");
            return Data.Reinterpret<T>(1);
        }

        public void Dispose()
        {
            if (Data.IsCreated) Data.Dispose();
            Data = default;
        }
    }
}
