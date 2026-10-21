using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace LiveSplit.UI.Components
{
    internal static class OcrNativeRuntime
    {
        private const uint LoadWithAlteredSearchPath = 8;
        private static readonly object Sync = new object();
        private static IntPtr tesseractModule;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        internal static void EnsureLoaded()
        {
            lock (Sync)
            {
                if (tesseractModule != IntPtr.Zero) return;

                string nativePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Components", IntPtr.Size == 8 ? "x64" : "x86", "tesseract41.dll"));
                if (!File.Exists(nativePath))
                    throw new Win32Exception(2, "OCR native runtime is missing: " + nativePath);

                // Resolve Leptonica and CRT imports beside Tesseract. Keep this reference for
                // the process lifetime, just like the wrapper's own native module references.
                IntPtr module = LoadLibraryEx(nativePath, IntPtr.Zero, LoadWithAlteredSearchPath);
                if (module == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Cannot load OCR native runtime or its dependencies: " + nativePath);

                tesseractModule = module;
            }
        }
    }
}
