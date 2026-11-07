using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace CrashTWoCLoadDetector
{
    internal sealed class NativeWindowsCaptureSession : IWindowsCaptureSession
    {
        private const int Success = 0, Pending = 1, BufferTooSmall = 2;
        private const long MaximumFrameBytes = 256L * 1024 * 1024;
        private readonly object sync = new object();
        private IntPtr library;
        private IntPtr context;
        private CreateFunction create;
        private CopyFunction copy;
        private DestroyFunction destroy;
        private byte[] readBuffer;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CreateFunction(int kind, IntPtr handle, out IntPtr context, [Out] byte[] error, uint errorCapacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CopyFunction(IntPtr context, IntPtr buffer, uint capacity, out uint width, out uint height,
            out uint stride, out ulong generation, [Out] byte[] error, uint errorCapacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DestroyFunction(IntPtr context);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        public NativeWindowsCaptureSession(WindowsCaptureTarget target)
        {
            try
            {
                string nativePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Components", IntPtr.Size == 8 ? "x64" : "x86", "TwocWindowsGraphicsCapture.dll"));
                if (!File.Exists(nativePath)) throw new FileNotFoundException("Windows Graphics Capture runtime is missing: " + nativePath);
                library = LoadLibraryEx(nativePath, IntPtr.Zero, 8);
                if (library == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load Windows Graphics Capture. Check the runtime architecture.");
                create = LoadFunction<CreateFunction>("TwgcCreate");
                copy = LoadFunction<CopyFunction>("TwgcCopyLatest");
                destroy = LoadFunction<DestroyFunction>("TwgcDestroy");
                var error = new byte[2048];
                int status = create((int)target.Kind, target.Handle, out context, error, (uint)error.Length);
                if (status != Success || context == IntPtr.Zero)
                    throw Failure("Cannot start Windows Graphics Capture", status, error);
            }
            catch (Exception exception)
            {
                Dispose();
                if (exception is CaptureUnavailableException) throw;
                throw new CaptureUnavailableException("Windows Graphics Capture is unavailable: " + exception.Message, exception);
            }
        }

        private T LoadFunction<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(library, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException("Windows Graphics Capture runtime does not export " + name);
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        public Bitmap Capture()
        {
            lock (sync)
            {
                if (context == IntPtr.Zero) throw new ObjectDisposedException("NativeWindowsCaptureSession");
                var error = new byte[2048];
                // A resize can replace the latest frame between querying and copying it.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    uint width, height, stride;
                    ulong generation;
                    Array.Clear(error, 0, error.Length);
                    int status = copy(context, IntPtr.Zero, 0, out width, out height, out stride, out generation, error, (uint)error.Length);
                    if (status == Pending) throw NotReady(error);
                    if (status != BufferTooSmall && status != Success) throw Failure("Cannot read the capture frame", status, error);
                    int required = ValidateFrame(width, height, stride);
                    if (readBuffer == null || readBuffer.Length < required) readBuffer = new byte[required];
                    byte[] pixels = readBuffer;
                    GCHandle pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try
                    {
                        Array.Clear(error, 0, error.Length);
                        status = copy(context, pinned.AddrOfPinnedObject(), (uint)pixels.Length,
                            out width, out height, out stride, out generation, error, (uint)error.Length);
                    }
                    finally { pinned.Free(); }
                    if (status == Pending) throw NotReady(error);
                    if (status == BufferTooSmall) continue;
                    if (status != Success) throw Failure("Cannot read the capture frame", status, error);
                    int actualSize = ValidateFrame(width, height, stride);
                    if (actualSize > pixels.Length) throw new CaptureUnavailableException("The capture runtime returned an invalid frame size.");
                    return CreateBitmap(pixels, (int)width, (int)height, (int)stride);
                }
                throw new CaptureNotReadyException("The capture size is changing; waiting for a stable frame.");
            }
        }

        private static int ValidateFrame(uint width, uint height, uint stride)
        {
            long rowBytes = (long)width * 4;
            long size = (long)stride * height;
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue ||
                stride != rowBytes || size <= 0 || size > MaximumFrameBytes)
                throw new CaptureUnavailableException("The capture runtime returned invalid or excessive frame dimensions.");
            return (int)size;
        }

        private static Bitmap CreateBitmap(byte[] pixels, int width, int height, int stride)
        {
            // WGC may leave meaningful/non-opaque alpha at rounded window edges.
            // Screen RGB is authoritative; downstream resizing must not blend it
            // against an invented background based on that alpha channel.
            var result = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            try
            {
                BitmapData data = result.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    for (int row = 0; row < height; row++)
                        Marshal.Copy(pixels, row * stride, IntPtr.Add(data.Scan0, row * data.Stride), stride);
                }
                finally { result.UnlockBits(data); }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static CaptureUnavailableException Failure(string action, int status, byte[] error)
        {
            string message = Decode(error);
            return new CaptureUnavailableException(action + " (status " + status + ")." + (message.Length == 0 ? "" : " " + message));
        }

        private static CaptureNotReadyException NotReady(byte[] error)
        {
            string message = Decode(error);
            return new CaptureNotReadyException(message.Length == 0 ? "Waiting for a Windows Graphics Capture frame." : message);
        }

        private static string Decode(byte[] error)
        {
            int end = Array.IndexOf(error, (byte)0);
            return Encoding.UTF8.GetString(error, 0, end < 0 ? error.Length : end);
        }

        public void Dispose()
        {
            IntPtr retiredContext, retiredLibrary;
            DestroyFunction retiredDestroy;
            CreateFunction retiredCreate;
            CopyFunction retiredCopy;
            lock (sync)
            {
                retiredContext = context;
                retiredLibrary = library;
                retiredDestroy = destroy;
                retiredCreate = create;
                retiredCopy = copy;
                context = IntPtr.Zero;
                library = IntPtr.Zero;
                readBuffer = null;
            }
            // Destroy joins the capture thread. Keep all native entry points and the
            // module alive until it has finished, without blocking the timer/UI thread.
            if (retiredLibrary != IntPtr.Zero)
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { if (retiredContext != IntPtr.Zero && retiredDestroy != null) retiredDestroy(retiredContext); }
                    finally
                    {
                        GC.KeepAlive(retiredCreate);
                        GC.KeepAlive(retiredCopy);
                        GC.KeepAlive(retiredDestroy);
                        FreeLibrary(retiredLibrary);
                    }
                });
            GC.SuppressFinalize(this);
        }

        ~NativeWindowsCaptureSession() { Dispose(); }
    }
}
