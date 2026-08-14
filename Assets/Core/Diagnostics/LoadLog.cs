using System;
using System.Collections.Generic;

namespace PointCloud.Core.Diagnostics
{
    public enum LogSeverity : byte { Info, Warning, Error }

    public readonly struct LogEntry
    {
        public readonly DateTime    TimeUtc;
        public readonly LogSeverity Severity;
        public readonly string      Scope;     // "PLY", "PCD", "Renderer", ...
        public readonly string      Message;

        public LogEntry(LogSeverity severity, string scope, string message)
        {
            TimeUtc  = DateTime.UtcNow;
            Severity = severity;
            Scope    = scope;
            Message  = message;
        }

        public override string ToString() => $"[{Scope}] {Message}";
    }

    /// <summary>
    /// Append-only log surfaced in the UI log panel. Parsers write every assumption and
    /// every dropped point here — engineers debug their own exporters with this, so the
    /// messages are part of the product, not diagnostics noise.
    ///
    /// Instance, not static: domain reload is disabled in this project, so a static log
    /// would accumulate across play sessions. Owned by AppServices.
    /// </summary>
    public sealed class LoadLog
    {
        readonly List<LogEntry> _entries = new(256);
        readonly object         _gate    = new();
        readonly int            _capacity;

        /// <summary>Raised on every append. May fire from a background thread — marshal before touching UI.</summary>
        public event Action<LogEntry> EntryAdded;

        public LoadLog(int capacity = 10000) => _capacity = capacity;

        public int Count { get { lock (_gate) return _entries.Count; } }

        public void Info(string scope, string message)    => Add(LogSeverity.Info, scope, message);
        public void Warning(string scope, string message) => Add(LogSeverity.Warning, scope, message);
        public void Error(string scope, string message)   => Add(LogSeverity.Error, scope, message);

        public void Add(LogSeverity severity, string scope, string message)
        {
            var entry = new LogEntry(severity, scope, message);
            lock (_gate)
            {
                if (_entries.Count >= _capacity)
                    _entries.RemoveRange(0, _capacity / 4);
                _entries.Add(entry);
            }
            EntryAdded?.Invoke(entry);
        }

        /// <summary>Snapshot for the UI. Copies under the lock so the caller can iterate freely.</summary>
        public LogEntry[] Snapshot()
        {
            lock (_gate) return _entries.ToArray();
        }

        public void Clear()
        {
            lock (_gate) _entries.Clear();
        }
    }
}
