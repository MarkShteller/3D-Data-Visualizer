using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace PointCloud.App.UI.Platform
{
    /// <summary>
    /// The Windows common open dialog, via comdlg32.
    ///
    /// Roughly sixty lines of P/Invoke rather than a third-party package: it is the native
    /// dialog users already know, it works identically in the editor and in a build, and it
    /// adds no dependency. StandaloneFileBrowser wraps this same call.
    /// </summary>
    public sealed class Win32FileDialog : IFileDialogService
    {
        // OFN flags. EXPLORER is required for the modern dialog and for the multi-select
        // buffer layout; NOCHANGEDIR stops the dialog altering the process working
        // directory, which would silently break every later relative path.
        const int OFN_READONLY         = 0x00000001;
        const int OFN_HIDEREADONLY     = 0x00000004;
        const int OFN_NOCHANGEDIR      = 0x00000008;
        const int OFN_ALLOWMULTISELECT = 0x00000200;
        const int OFN_PATHMUSTEXIST    = 0x00000800;
        const int OFN_FILEMUSTEXIST    = 0x00001000;
        const int OFN_EXPLORER         = 0x00080000;

        /// <summary>Multi-select returns one buffer holding every path, so it must be generous.</summary>
        const int BufferChars = 64 * 1024;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenFileName
        {
            public int    lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int    nMaxCustFilter;
            public int    nFilterIndex;
            public IntPtr lpstrFile;
            public int    nMaxFile;
            public string lpstrFileTitle;
            public int    nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int    Flags;
            public short  nFileOffset;
            public short  nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int    dwReserved;
            public int    flagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetOpenFileNameW(ref OpenFileName ofn);

        [DllImport("comdlg32.dll")]
        static extern int CommDlgExtendedError();

        [DllImport("user32.dll")]
        static extern IntPtr GetActiveWindow();

        public bool IsAvailable => Application.platform is RuntimePlatform.WindowsPlayer
                                                        or RuntimePlatform.WindowsEditor;

        public string[] OpenFiles(string title, string[] extensions, bool allowMultiple,
                                  string initialDirectory = null)
        {
            if (!IsAvailable) return Array.Empty<string>();

            IntPtr buffer = Marshal.AllocHGlobal(BufferChars * sizeof(char));

            try
            {
                // The buffer must start empty or the dialog treats the garbage as a filename.
                Marshal.WriteInt16(buffer, 0, 0);

                var ofn = new OpenFileName
                {
                    lStructSize     = Marshal.SizeOf<OpenFileName>(),
                    // Owning the dialog to Unity's window keeps it properly modal instead of
                    // letting it slip behind the main window.
                    hwndOwner       = GetActiveWindow(),
                    lpstrFilter     = BuildFilter(extensions),
                    nFilterIndex    = 1,
                    lpstrFile       = buffer,
                    nMaxFile        = BufferChars,
                    lpstrInitialDir = Directory.Exists(initialDirectory) ? initialDirectory : null,
                    lpstrTitle      = title,
                    Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST |
                            OFN_NOCHANGEDIR | OFN_HIDEREADONLY | OFN_READONLY |
                            (allowMultiple ? OFN_ALLOWMULTISELECT : 0),
                };

                if (!GetOpenFileNameW(ref ofn))
                {
                    int error = CommDlgExtendedError();
                    // 0 means the user simply cancelled, which is not a failure.
                    if (error != 0) Debug.LogWarning($"[FileDialog] GetOpenFileName failed with code 0x{error:X}");
                    return Array.Empty<string>();
                }

                return ParseResult(buffer);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[FileDialog] Native dialog unavailable: {e.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// Filters are double-null-terminated pairs of label and pattern. Building it by
        /// hand because the trailing double null is what terminates the list, and a normal
        /// C# string literal cannot carry it.
        /// </summary>
        static string BuildFilter(string[] extensions)
        {
            var builder = new StringBuilder();

            if (extensions != null && extensions.Length > 0)
            {
                var patterns = new List<string>(extensions.Length);
                foreach (var extension in extensions)
                    patterns.Add("*" + (extension.StartsWith(".") ? extension : "." + extension));

                builder.Append("Point clouds (").Append(string.Join(", ", patterns)).Append(')').Append('\0')
                       .Append(string.Join(";", patterns)).Append('\0');

                foreach (var pattern in patterns)
                    builder.Append(pattern).Append('\0').Append(pattern).Append('\0');
            }

            builder.Append("All files (*.*)").Append('\0').Append("*.*").Append('\0').Append('\0');
            return builder.ToString();
        }

        /// <summary>
        /// Single select returns one full path. Multi-select returns the directory, then a
        /// null-separated list of file names, then a second null.
        /// </summary>
        static string[] ParseResult(IntPtr buffer)
        {
            var parts = new List<string>();
            int offset = 0;

            while (offset < BufferChars)
            {
                var builder = new StringBuilder();
                while (offset < BufferChars)
                {
                    char c = (char)Marshal.ReadInt16(buffer, offset * sizeof(char));
                    offset++;
                    if (c == '\0') break;
                    builder.Append(c);
                }

                if (builder.Length == 0) break;   // second null: end of list
                parts.Add(builder.ToString());
            }

            if (parts.Count == 0) return Array.Empty<string>();
            if (parts.Count == 1) return new[] { parts[0] };

            var directory = parts[0];
            var paths = new string[parts.Count - 1];
            for (int i = 1; i < parts.Count; i++) paths[i - 1] = Path.Combine(directory, parts[i]);
            return paths;
        }
    }
}
