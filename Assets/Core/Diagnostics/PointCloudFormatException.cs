using System;

namespace PointCloud.Core.Diagnostics
{
    /// <summary>
    /// Thrown when a source file is malformed, truncated, or uses a feature this
    /// tool does not support. Always carries the byte offset where the problem was
    /// detected so a CV engineer can open the file in a hex editor and see it.
    /// </summary>
    public class PointCloudFormatException : Exception
    {
        /// <summary>Byte offset into the source where the problem was detected, or -1 if unknown.</summary>
        public long ByteOffset { get; }

        /// <summary>Format id of the parser that raised this ("ply", "pcd", "obj", "fbx", "vrs").</summary>
        public string FormatId { get; }

        public PointCloudFormatException(string formatId, string message, long byteOffset = -1)
            : base(Compose(formatId, message, byteOffset))
        {
            FormatId = formatId;
            ByteOffset = byteOffset;
        }

        public PointCloudFormatException(string formatId, string message, long byteOffset, Exception inner)
            : base(Compose(formatId, message, byteOffset), inner)
        {
            FormatId = formatId;
            ByteOffset = byteOffset;
        }

        static string Compose(string formatId, string message, long byteOffset)
        {
            var prefix = string.IsNullOrEmpty(formatId) ? "" : formatId.ToUpperInvariant() + ": ";
            return byteOffset >= 0
                ? $"{prefix}{message} (at byte 0x{byteOffset:X} / {byteOffset})"
                : $"{prefix}{message}";
        }
    }

    /// <summary>
    /// Thrown when a file is recognised but the feature it uses is deliberately out of
    /// scope. Distinct from <see cref="PointCloudFormatException"/> so the UI can say
    /// "not supported yet" rather than "your file is broken" — which matters, because
    /// those two messages send the user down completely different paths.
    /// </summary>
    public sealed class PointCloudUnsupportedException : PointCloudFormatException
    {
        public PointCloudUnsupportedException(string formatId, string message, long byteOffset = -1)
            : base(formatId, message, byteOffset) { }
    }
}
