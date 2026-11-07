using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
#if MANAGED_CAPTURE
using CrashTWoCLoadDetector;
#endif

// Opt-in integration test. Every nonzero capture handle belongs to the single
// synthetic form below. Never enumerates windows, monitors, processes or screens.
public static class GraphicsCaptureNativeSmoke
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int CreateFunction(int kind, UIntPtr hwnd, out IntPtr context, StringBuilder error, uint errorCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate int CopyFunction(IntPtr context, [Out] byte[] pixels, uint capacity, out uint width, out uint height, out uint stride, out ulong generation, StringBuilder error, uint errorCapacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void DestroyFunction(IntPtr context);
    static CreateFunction TwgcCreate;
    static CopyFunction TwgcCopyLatest;
    static DestroyFunction TwgcDestroy;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    const int Pending = 1, BufferTooSmall = 2, Unavailable = 3;
    static int checks;
    sealed class PatternWindow : Form
    {
        public PatternWindow() {
            Text = "Owned synthetic WGC verification";
            StartPosition = FormStartPosition.Manual; Location = new Point(60, 60);
            ClientSize = new Size(321, 203); ShowInTaskbar = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e) {
            int w = ClientSize.Width, h = ClientSize.Height;
            e.Graphics.FillRectangle(Brushes.Red, 0, 0, w / 2, h / 2);
            e.Graphics.FillRectangle(Brushes.Lime, w / 2, 0, w - w / 2, h / 2);
            e.Graphics.FillRectangle(Brushes.Blue, 0, h / 2, w / 2, h - h / 2);
            e.Graphics.FillRectangle(Brushes.Yellow, w / 2, h / 2, w - w / 2, h - h / 2);
            using (var pen = new Pen(Color.Magenta, 4)) e.Graphics.DrawRectangle(pen, 0, 0, w - 1, h - 1);
        }
    }
    static void Check(bool condition, string message) {
        if (!condition) throw new Exception("FAIL: " + message);
        checks++; Console.WriteLine("PASS: " + message);
    }
    static void Pump(PatternWindow form) { Application.DoEvents(); if (!form.IsDisposed && form.WindowState != FormWindowState.Minimized) form.Refresh(); Thread.Sleep(15); }
    static byte[] WaitFrame(IntPtr context, PatternWindow form, out uint width, out uint height, out ulong generation) {
        var timer = Stopwatch.StartNew(); var error = new StringBuilder(1024);
        uint stride; int last = Pending; string message = "";
        width = height = 0; generation = 0;
        while (timer.ElapsedMilliseconds < 10000) {
            Pump(form);
            last = TwgcCopyLatest(context, null, 0, out width, out height, out stride, out generation, error, 1024);
            message = error.ToString();
            if (last == Pending) { CheckEmpty(width, height, stride, generation, "pending query"); continue; }
            if (last != BufferTooSmall) throw new Exception("Native query status=" + last + ": " + message);
            Check(stride == width * 4 && width > 0 && height > 0 && generation > 0, "query describes packed BGRA8 and a generated frame");
            var buffer = new byte[checked((int)(stride * height))];
            last = TwgcCopyLatest(context, buffer, (uint)buffer.Length, out width, out height, out stride, out generation, error, 1024);
            if (last == 0) return buffer;
            if (last != Pending && last != BufferTooSmall) throw new Exception("Native copy status=" + last + ": " + error);
        }
        throw new Exception("Timed out waiting for the owned synthetic HWND. Last status=" + last + ": " + message);
    }
    static void CheckEmpty(uint width, uint height, uint stride, ulong generation, string message) {
        if (width != 0 || height != 0 || stride != 0 || generation != 0) throw new Exception(message + " exposed stale frame metadata");
    }
    static bool Pixel(byte[] bytes, uint width, int x, int y, Color expected) {
        int offset = checked((int)((y * width + x) * 4));
        return Math.Abs(bytes[offset] - expected.B) <= 2 && Math.Abs(bytes[offset + 1] - expected.G) <= 2 && Math.Abs(bytes[offset + 2] - expected.R) <= 2;
    }
    static string PixelText(byte[] bytes, uint width, int x, int y) {
        int offset = checked((int)((y * width + x) * 4));
        return "(" + x + "," + y + ") BGRA=" + bytes[offset] + "," + bytes[offset + 1] + "," + bytes[offset + 2] + "," + bytes[offset + 3];
    }
    static void VerifyPattern(byte[] bytes, uint width, uint height, IntPtr hwnd, string phase) {
        RECT rect; Check(GetClientRect(hwnd, out rect), phase + " owned client bounds are available");
        Check(width == rect.Right - rect.Left && height == rect.Bottom - rect.Top, phase + " frame is exactly the physical client size, excluding titlebar/borders");
        // DWM can clip the extreme client corners on rounded Windows 11 windows.
        // Probe each edge midpoint instead: titlebar or crop-origin errors still fail.
        Check(Pixel(bytes, width, (int)width / 2, 1, Color.Magenta)
            && Pixel(bytes, width, (int)width / 2, (int)height - 2, Color.Magenta)
            && Pixel(bytes, width, 1, (int)height / 2, Color.Magenta)
            && Pixel(bytes, width, (int)width - 2, (int)height / 2, Color.Magenta), phase + " all four client edge markers have correct origin and extent; " + PixelText(bytes, width, (int)width / 2, (int)height - 2));
        Check(Pixel(bytes, width, (int)width / 4, (int)height / 4, Color.Red)
            && Pixel(bytes, width, (int)width * 3 / 4, (int)height / 4, Color.Lime)
            && Pixel(bytes, width, (int)width / 4, (int)height * 3 / 4, Color.Blue)
            && Pixel(bytes, width, (int)width * 3 / 4, (int)height * 3 / 4, Color.Yellow), phase + " quadrants preserve BGRA channel order and row orientation");
    }
    static IntPtr Create(IntPtr hwnd) {
        var error = new StringBuilder(1024); IntPtr context;
        int status = TwgcCreate(1, new UIntPtr(unchecked((ulong)hwnd.ToInt64())), out context, error, 1024);
        Check(status == 0 && context != IntPtr.Zero, "created asynchronous session for owned HWND; status=" + status + " " + error);
        return context;
    }
#if MANAGED_CAPTURE
    static Bitmap WaitManaged(WindowsGraphicsCaptureProvider provider, WindowsCaptureTarget target, PatternWindow form) {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < 10000) {
            Pump(form);
            try { return provider.Capture(target); } catch (CaptureNotReadyException) { }
        }
        throw new Exception("Timed out waiting for managed provider's owned synthetic HWND frame.");
    }
    static bool ColorAt(Bitmap frame, int x, int y, Color expected) {
        var actual = frame.GetPixel(x, y);
        return Math.Abs(actual.R - expected.R) <= 2 && Math.Abs(actual.G - expected.G) <= 2 && Math.Abs(actual.B - expected.B) <= 2;
    }
    static void VerifyManaged(PatternWindow form) {
        var target = new WindowsCaptureTarget(WindowsCaptureTargetKind.Window, form.Handle);
        using (var provider = new WindowsGraphicsCaptureProvider()) {
            using (var frame = WaitManaged(provider, target, form)) {
                RECT rect; GetClientRect(form.Handle, out rect);
                Check(frame.Width == rect.Right && frame.Height == rect.Bottom, "real managed provider resolves packaged DLL and preserves physical client dimensions");
                Check(ColorAt(frame, frame.Width / 4, frame.Height / 4, Color.Red) && ColorAt(frame, frame.Width * 3 / 4, frame.Height / 4, Color.Lime)
                    && ColorAt(frame, frame.Width / 4, frame.Height * 3 / 4, Color.Blue) && ColorAt(frame, frame.Width * 3 / 4, frame.Height * 3 / 4, Color.Yellow), "real managed BGRA marshalling preserves all quadrant RGB values");
                var info = new ImageCaptureInfo { featureVectorResolutionX = 640, featureVectorResolutionY = 480, captureSizeX = 100, captureSizeY = 50,
                    captureAspectRatio = 4f / 3, resizeSizeXEng = 80, resizeSizeYEng = 20, resizeSizeXJpn = 90, resizeSizeYJpn = 24 };
                using (var detector = CapturedFrameProcessor.Process(frame, ref info, new Rectangle(0, 0, 400, 160), CapturedFramePurpose.Detector)) {
                    Check(detector.Width == 80 && detector.Height == 20 && ColorAt(detector, 10, 3, Color.Red) && ColorAt(detector, 70, 3, Color.Lime)
                        && ColorAt(detector, 10, 17, Color.Blue) && ColorAt(detector, 70, 17, Color.Yellow), "live managed frame reaches detector ROI with correct geometry and colors");
                }
                provider.Reset();
                Check(ColorAt(frame, frame.Width / 4, frame.Height / 4, Color.Red), "returned managed bitmap remains owned and usable after session reset");
            }
            using (var recreated = WaitManaged(provider, target, form)) Check(recreated.Width == form.ClientSize.Width, "real managed provider recreates its native session after Reset");
            form.WindowState = FormWindowState.Minimized; Application.DoEvents();
            bool pending = false;
            try { using (var stale = provider.Capture(target)) { } } catch (CaptureNotReadyException) { pending = true; }
            Check(pending, "real managed provider exposes minimized Pending as typed exception without stale bitmap");
            form.WindowState = FormWindowState.Normal; form.Refresh();
            using (var restored = WaitManaged(provider, target, form)) Check(restored.Width == form.ClientSize.Width, "real managed session recovers after restore");
        }
    }
#endif
    [STAThread] public static int Main() {
        IntPtr context = IntPtr.Zero;
        IntPtr library = IntPtr.Zero;
        try {
            library = LoadLibraryEx(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TwocWindowsGraphicsCapture.dll"), IntPtr.Zero, 8);
            if (library == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            TwgcCreate = (CreateFunction)Marshal.GetDelegateForFunctionPointer(GetProcAddress(library, "TwgcCreate"), typeof(CreateFunction));
            TwgcCopyLatest = (CopyFunction)Marshal.GetDelegateForFunctionPointer(GetProcAddress(library, "TwgcCopyLatest"), typeof(CopyFunction));
            TwgcDestroy = (DestroyFunction)Marshal.GetDelegateForFunctionPointer(GetProcAddress(library, "TwgcDestroy"), typeof(DestroyFunction));
            // Establish physical pixel coordinates before creating any HWND.
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            using (var form = new PatternWindow()) {
                form.Show(); form.Refresh(); Application.DoEvents();
                IntPtr ownedHwnd = form.Handle;
                Console.WriteLine("Capturing only owned synthetic HWND 0x" + ownedHwnd.ToInt64().ToString("X") + "; process bits=" + (IntPtr.Size * 8));
                context = Create(ownedHwnd);
                uint width, height; ulong generation;
                var bytes = WaitFrame(context, form, out width, out height, out generation);
                VerifyPattern(bytes, width, height, ownedHwnd, "initial odd-size");
                ulong firstGeneration = generation;
                form.ClientSize = new Size(437, 269); form.Refresh();
                bytes = WaitFrame(context, form, out width, out height, out generation);
                VerifyPattern(bytes, width, height, ownedHwnd, "resized odd-size");
                Check(generation > firstGeneration, "resize publishes a newer frame generation");
                form.WindowState = FormWindowState.Minimized; Application.DoEvents();
                uint stride; var error = new StringBuilder(1024); var sentinel = new byte[32];
                for (int i = 0; i < sentinel.Length; i++) sentinel[i] = 0xA7;
                int status = TwgcCopyLatest(context, sentinel, (uint)sentinel.Length, out width, out height, out stride, out generation, error, 1024);
                Check(status == Pending, "minimized owned window immediately returns Pending instead of stale pixels");
                CheckEmpty(width, height, stride, generation, "minimized window");
                Check(Array.TrueForAll(sentinel, value => value == 0xA7), "Pending leaves destination buffer untouched");
                form.WindowState = FormWindowState.Normal; form.Refresh();
                bytes = WaitFrame(context, form, out width, out height, out generation);
                VerifyPattern(bytes, width, height, ownedHwnd, "restored");
                TwgcDestroy(context); context = IntPtr.Zero;
                for (int i = 0; i < 3; i++) {
                    context = Create(ownedHwnd);
                    bytes = WaitFrame(context, form, out width, out height, out generation);
                    VerifyPattern(bytes, width, height, ownedHwnd, "recreated session " + (i + 1));
                    if (i != 2) { TwgcDestroy(context); context = IntPtr.Zero; }
                }
#if MANAGED_CAPTURE
                VerifyManaged(form);
#endif
                form.Close(); Application.DoEvents();
                status = TwgcCopyLatest(context, null, 0, out width, out height, out stride, out generation, error, 1024);
                Check(status == Unavailable, "closing owned HWND immediately returns Unavailable");
                CheckEmpty(width, height, stride, generation, "closed window");
            }
            TwgcDestroy(context); context = IntPtr.Zero;
            Check(FreeLibrary(library), "released harness native loader reference after destroying all direct sessions"); library = IntPtr.Zero;
            Check(GetModuleHandle("TwocWindowsGraphicsCapture.dll") != IntPtr.Zero, "native bridge remains process-pinned for queued WinRT callback safety");
            Console.WriteLine("PASS: " + checks + " native synthetic-window checks"); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (context != IntPtr.Zero) TwgcDestroy(context); if (library != IntPtr.Zero) FreeLibrary(library); }
    }
}
