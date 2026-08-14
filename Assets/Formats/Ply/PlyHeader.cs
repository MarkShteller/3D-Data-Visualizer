using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PointCloud.Core.Diagnostics;

namespace PointCloud.Formats.Ply
{
    public enum PlyFormat { Ascii, BinaryLittleEndian, BinaryBigEndian }

    /// <summary>PLY scalar types. Sizes are fixed by the spec regardless of platform.</summary>
    public enum PlyScalarType : byte
    {
        None = 0,
        Int8, UInt8, Int16, UInt16, Int32, UInt32, Float32, Float64,
    }

    public static class PlyScalar
    {
        public static int Size(PlyScalarType type) => type switch
        {
            PlyScalarType.Int8 or PlyScalarType.UInt8   => 1,
            PlyScalarType.Int16 or PlyScalarType.UInt16 => 2,
            PlyScalarType.Int32 or PlyScalarType.UInt32 or PlyScalarType.Float32 => 4,
            PlyScalarType.Float64 => 8,
            _ => 0,
        };

        public static bool IsInteger(PlyScalarType type) =>
            type is PlyScalarType.Int8 or PlyScalarType.UInt8 or PlyScalarType.Int16
                 or PlyScalarType.UInt16 or PlyScalarType.Int32 or PlyScalarType.UInt32;

        /// <summary>
        /// Parse a type token. Both the long names and the C-style aliases appear in the
        /// wild — Blender writes "float", CloudCompare writes "float32", Open3D writes
        /// "double" — so all of them are accepted.
        /// </summary>
        public static PlyScalarType Parse(string token) => token switch
        {
            "char" or "int8"     => PlyScalarType.Int8,
            "uchar" or "uint8"   => PlyScalarType.UInt8,
            "short" or "int16"   => PlyScalarType.Int16,
            "ushort" or "uint16" => PlyScalarType.UInt16,
            "int" or "int32"     => PlyScalarType.Int32,
            "uint" or "uint32"   => PlyScalarType.UInt32,
            "float" or "float32" => PlyScalarType.Float32,
            "double" or "float64" => PlyScalarType.Float64,
            _ => PlyScalarType.None,
        };
    }

    public sealed class PlyProperty
    {
        public string Name;
        public PlyScalarType Type;

        /// <summary>True for `property list`, which makes the element variable-length.</summary>
        public bool IsList;
        public PlyScalarType ListCountType;

        public override string ToString() =>
            IsList ? $"list {ListCountType} {Type} {Name}" : $"{Type} {Name}";
    }

    public sealed class PlyElement
    {
        public string Name;
        public long Count;
        public readonly List<PlyProperty> Properties = new();

        /// <summary>False when any property is a list, which means records have no fixed size.</summary>
        public bool HasFixedStride
        {
            get
            {
                foreach (var property in Properties)
                    if (property.IsList) return false;
                return true;
            }
        }

        /// <summary>Bytes per record in a binary file. Meaningless unless <see cref="HasFixedStride"/>.</summary>
        public int Stride
        {
            get
            {
                int stride = 0;
                foreach (var property in Properties) stride += PlyScalar.Size(property.Type);
                return stride;
            }
        }
    }

    public sealed class PlyHeader
    {
        public PlyFormat Format;
        public string Version = "1.0";
        public readonly List<PlyElement> Elements = new();
        public readonly List<string> Comments = new();

        /// <summary>Byte offset where data begins, immediately after the end_header line.</summary>
        public long DataOffset;

        public PlyElement Vertex
        {
            get
            {
                foreach (var element in Elements)
                    if (element.Name == "vertex") return element;
                return null;
            }
        }
    }

    /// <summary>
    /// Reads the PLY header, which is always ASCII regardless of the data format.
    ///
    /// Read byte-by-byte rather than through a StreamReader: a StreamReader buffers ahead,
    /// so its position after "end_header" bears no relation to where the binary data
    /// actually starts. Getting that offset exactly right is the whole job here.
    /// </summary>
    public static class PlyHeaderParser
    {
        public const int MaxHeaderBytes = 1 << 20;   // 1 MB; a real header is a few hundred bytes

        public static PlyHeader Parse(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            var header = new PlyHeader();
            long offset = 0;

            string magic = ReadLine(stream, ref offset);
            if (magic == null || magic.Trim() != "ply")
                throw new PointCloudFormatException("ply",
                    $"Missing 'ply' magic; file starts with '{Truncate(magic)}'.", 0);

            PlyElement current = null;
            bool sawFormat = false;

            while (true)
            {
                if (offset > MaxHeaderBytes)
                    throw new PointCloudFormatException("ply",
                        "No 'end_header' within the first megabyte; this is not a PLY file.", offset);

                long lineStart = offset;
                string line = ReadLine(stream, ref offset);

                if (line == null)
                    throw new PointCloudFormatException("ply",
                        "File ended before 'end_header'.", lineStart);

                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var tokens = trimmed.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

                switch (tokens[0])
                {
                    case "comment":
                    case "obj_info":
                        // A comment can legally contain the word end_header, so comments are
                        // consumed here before the terminator is ever tested.
                        header.Comments.Add(trimmed.Substring(tokens[0].Length).Trim());
                        continue;

                    case "format":
                        if (tokens.Length < 2)
                            throw new PointCloudFormatException("ply", "Malformed 'format' line.", lineStart);
                        header.Format = tokens[1] switch
                        {
                            "ascii"                => PlyFormat.Ascii,
                            "binary_little_endian" => PlyFormat.BinaryLittleEndian,
                            "binary_big_endian"    => PlyFormat.BinaryBigEndian,
                            _ => throw new PointCloudFormatException("ply",
                                     $"Unknown format '{tokens[1]}'.", lineStart),
                        };
                        if (tokens.Length > 2) header.Version = tokens[2];
                        sawFormat = true;
                        continue;

                    case "element":
                        if (tokens.Length < 3)
                            throw new PointCloudFormatException("ply", "Malformed 'element' line.", lineStart);
                        if (!long.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                                           out long count) || count < 0)
                            throw new PointCloudFormatException("ply",
                                $"Element '{tokens[1]}' has an invalid count '{tokens[2]}'.", lineStart);

                        current = new PlyElement { Name = tokens[1], Count = count };
                        header.Elements.Add(current);
                        continue;

                    case "property":
                        if (current == null)
                            throw new PointCloudFormatException("ply",
                                "'property' appeared before any 'element'.", lineStart);
                        current.Properties.Add(ParseProperty(tokens, lineStart));
                        continue;

                    case "end_header":
                        if (!sawFormat)
                            throw new PointCloudFormatException("ply", "Header has no 'format' line.", lineStart);
                        header.DataOffset = offset;
                        return header;

                    default:
                        // Unknown keywords are skipped rather than fatal: PLY is extended
                        // freely in practice and an unrecognised line is not a reason to
                        // refuse a file whose vertex data is perfectly readable.
                        continue;
                }
            }
        }

        static PlyProperty ParseProperty(string[] tokens, long lineStart)
        {
            if (tokens.Length >= 5 && tokens[1] == "list")
            {
                var countType = PlyScalar.Parse(tokens[2]);
                var valueType = PlyScalar.Parse(tokens[3]);
                if (countType == PlyScalarType.None || valueType == PlyScalarType.None)
                    throw new PointCloudFormatException("ply",
                        $"Unknown type in list property '{string.Join(" ", tokens)}'.", lineStart);

                return new PlyProperty
                {
                    Name = tokens[4], Type = valueType,
                    IsList = true, ListCountType = countType,
                };
            }

            if (tokens.Length < 3)
                throw new PointCloudFormatException("ply",
                    $"Malformed property '{string.Join(" ", tokens)}'.", lineStart);

            var type = PlyScalar.Parse(tokens[1]);
            if (type == PlyScalarType.None)
                throw new PointCloudFormatException("ply",
                    $"Unknown property type '{tokens[1]}'.", lineStart);

            return new PlyProperty { Name = tokens[2], Type = type };
        }

        /// <summary>
        /// Read one line, advancing <paramref name="offset"/> past its terminator. Handles
        /// LF and CRLF; a lone CR is treated as a terminator too, which some old exporters
        /// still emit.
        /// </summary>
        static string ReadLine(Stream stream, ref long offset)
        {
            var builder = new StringBuilder(96);

            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) return builder.Length > 0 ? builder.ToString() : null;

                offset++;

                if (b == '\n') return builder.ToString();

                if (b == '\r')
                {
                    int next = stream.ReadByte();
                    if (next == '\n') offset++;
                    else if (next >= 0) stream.Seek(-1, SeekOrigin.Current);
                    return builder.ToString();
                }

                builder.Append((char)b);
            }
        }

        static string Truncate(string value) =>
            value == null ? "<empty>" : value.Length <= 24 ? value : value.Substring(0, 24) + "…";
    }
}
