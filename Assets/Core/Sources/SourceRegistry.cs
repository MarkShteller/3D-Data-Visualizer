using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PointCloud.Core.Diagnostics;

namespace PointCloud.Core.Sources
{
    /// <summary>
    /// Maps a file path to the source that can read it.
    ///
    /// An instance rather than a static singleton: this project runs with domain reload
    /// disabled, so a static registry would accumulate duplicate factory registrations
    /// across play sessions. It is owned by AppServices and dies with it.
    /// </summary>
    public sealed class SourceRegistry
    {
        /// <summary>Bytes sniffed from the head of a file and handed to CanHandle.</summary>
        public const int MagicLength = 64;

        readonly List<IPointCloudSourceFactory> _factories = new();

        public IReadOnlyList<IPointCloudSourceFactory> Factories => _factories;

        public void Register(IPointCloudSourceFactory factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            if (_factories.Any(f => f.Id == factory.Id))
                throw new InvalidOperationException($"A source factory with id '{factory.Id}' is already registered.");

            _factories.Add(factory);
            _factories.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        public bool Unregister(string id) => _factories.RemoveAll(f => f.Id == id) > 0;

        /// <summary>Every extension any factory claims, lower-case with the dot, for the file dialog.</summary>
        public string[] SupportedExtensions =>
            _factories.SelectMany(f => f.Extensions).Distinct().OrderBy(e => e).ToArray();

        /// <summary>
        /// Find the factory for a path. Reads the first bytes so a mis-named file is
        /// recognised by content, and returns false rather than throwing so the caller can
        /// phrase the error.
        /// </summary>
        public bool TryResolve(string path, out IPointCloudSourceFactory factory)
        {
            factory = null;
            if (string.IsNullOrWhiteSpace(path)) return false;

            var magic = ReadMagic(path);

            foreach (var candidate in _factories)
            {
                if (!candidate.CanHandle(path, magic)) continue;
                factory = candidate;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Create a source for a path, or throw with a message that names what was actually
        /// tried — a bare "unsupported format" tells the user nothing about what to do next.
        /// </summary>
        public IPointCloudSource Create(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Path is empty.", nameof(path));

            if (!File.Exists(path))
                throw new FileNotFoundException($"No such file: {path}", path);

            if (TryResolve(path, out var factory))
                return factory.Create(path);

            var extension = Path.GetExtension(path);
            throw new PointCloudUnsupportedException("registry",
                $"Nothing can read '{Path.GetFileName(path)}' (extension '{extension}'). " +
                $"Supported: {string.Join(", ", SupportedExtensions)}");
        }

        static byte[] ReadMagic(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[MagicLength];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read == buffer.Length) return buffer;

                // A file shorter than the sniff window is legal; hand back only what exists
                // so a factory does not match on uninitialised zeros.
                Array.Resize(ref buffer, Math.Max(0, read));
                return buffer;
            }
            catch (IOException)
            {
                // Locked or unreadable: fall back to extension matching rather than failing
                // discovery outright, so the real IO error surfaces from OpenAsync instead.
                return Array.Empty<byte>();
            }
        }

        /// <summary>Case-insensitive extension test, for factories to use in CanHandle.</summary>
        public static bool HasExtension(string path, params string[] extensions)
        {
            var actual = Path.GetExtension(path);
            foreach (var extension in extensions)
                if (string.Equals(actual, extension, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
