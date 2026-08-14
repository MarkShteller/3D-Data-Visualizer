using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PointCloud.App.UI.Platform
{
    /// <summary>
    /// Most-recently-opened file list, persisted next to the player's other data.
    ///
    /// Worth more than it looks for this audience: evaluating a change usually means
    /// reopening the same two or three files repeatedly, and a dialog that starts from
    /// scratch every time turns that into a chore.
    /// </summary>
    public sealed class RecentFiles
    {
        public const int MaxEntries = 12;

        readonly string _storePath;
        readonly List<string> _paths = new();

        public IReadOnlyList<string> Paths => _paths;

        public RecentFiles(string storePath = null)
        {
            _storePath = storePath ?? Path.Combine(Application.persistentDataPath, "recent-files.txt");
            Load();
        }

        public void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var full = SafeFullPath(path);
            _paths.RemoveAll(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase));
            _paths.Insert(0, full);

            if (_paths.Count > MaxEntries) _paths.RemoveRange(MaxEntries, _paths.Count - MaxEntries);
            Save();
        }

        /// <summary>Drop entries whose file no longer exists. Called before showing the list.</summary>
        public void PruneMissing()
        {
            int removed = _paths.RemoveAll(path => !File.Exists(path));
            if (removed > 0) Save();
        }

        /// <summary>The directory of the most recent entry, for the dialog to start in.</summary>
        public string LastDirectory
        {
            get
            {
                foreach (var path in _paths)
                {
                    var directory = Path.GetDirectoryName(path);
                    if (Directory.Exists(directory)) return directory;
                }
                return null;
            }
        }

        void Load()
        {
            try
            {
                if (!File.Exists(_storePath)) return;
                foreach (var line in File.ReadAllLines(_storePath))
                    if (!string.IsNullOrWhiteSpace(line)) _paths.Add(line.Trim());
            }
            catch (IOException)
            {
                // A missing or unreadable recent list is not worth surfacing to the user.
            }
        }

        void Save()
        {
            try
            {
                var directory = Path.GetDirectoryName(_storePath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(_storePath, _paths);
            }
            catch (IOException e)
            {
                Debug.LogWarning($"[RecentFiles] Could not save: {e.Message}");
            }
        }

        static string SafeFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch (ArgumentException) { return path; }
            catch (NotSupportedException) { return path; }
        }
    }
}
