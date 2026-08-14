using System;

namespace PointCloud.App.UI.Platform
{
    /// <summary>
    /// Opening files from the UI.
    ///
    /// Unity ships no runtime file dialog — EditorUtility.OpenFilePanel is editor-only — so
    /// this is an interface with a platform implementation behind it rather than a one-line
    /// call. Keeping it abstract also means the app degrades to a typed path rather than
    /// becoming unusable if the native dialog is unavailable.
    /// </summary>
    public interface IFileDialogService
    {
        bool IsAvailable { get; }

        /// <summary>
        /// Show a modal open dialog. Returns an empty array when cancelled.
        /// Must be called from the main thread; the native dialog blocks until dismissed.
        /// </summary>
        string[] OpenFiles(string title, string[] extensions, bool allowMultiple, string initialDirectory = null);
    }

    /// <summary>Used when no native dialog exists. The UI falls back to its path field.</summary>
    public sealed class NullFileDialogService : IFileDialogService
    {
        public bool IsAvailable => false;

        public string[] OpenFiles(string title, string[] extensions, bool allowMultiple,
                                  string initialDirectory = null) => Array.Empty<string>();
    }
}
