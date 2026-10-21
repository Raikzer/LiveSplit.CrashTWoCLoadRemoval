using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LiveSplit.CrashTWoCLoadRemover
{
    /// <summary>One confidence per capture; every neural-network layer runs on Vulkan.</summary>
    public sealed class VulkanLoadingClassifier : IDisposable
    {
        public const int InputWidth = 96;
        public const int InputHeight = 96;
        private readonly object sync = new object();
        private IntPtr library;
        private IntPtr context;
        private CreateFunction create;
        private PredictFunction predict;
        private DestroyFunction destroy;
        private DeviceNameFunction deviceName;
        public string DeviceName { get; private set; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CreateFunction([MarshalAs(UnmanagedType.LPWStr)] string model,
            [MarshalAs(UnmanagedType.LPWStr)] string shaders, int validation, out IntPtr handle,
            [Out] byte[] error, uint capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PredictFunction(IntPtr handle, [In] float[] pixels, uint count,
            out float confidence, [Out] byte[] error, uint capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DeviceNameFunction(IntPtr handle, [Out] byte[] name, uint capacity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void DestroyFunction(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        public static string ComponentsDirectory
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Components"); }
        }

        public static string ResolveModelPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Select an ML model file.");
            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(ComponentsDirectory, value));
        }

        public VulkanLoadingClassifier(string modelPath, string shaderDirectory, bool validation = false)
        {
            try
            {
                modelPath = Path.GetFullPath(modelPath);
                shaderDirectory = Path.GetFullPath(shaderDirectory);
                if (!File.Exists(modelPath)) throw new FileNotFoundException("ML model is missing: " + modelPath);
                string nativePath = Path.Combine(ComponentsDirectory, IntPtr.Size == 8 ? "x64" : "x86", "TwocLoadingVulkan.dll");
                if (!File.Exists(nativePath)) throw new FileNotFoundException("Vulkan classifier runtime is missing: " + nativePath);
                library = LoadLibraryEx(nativePath, IntPtr.Zero, 8); // Absolute path; resolve its imports beside it.
                if (library == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the Vulkan classifier. Check the graphics driver and runtime architecture.");
                create = LoadFunction<CreateFunction>("TwmCreate");
                predict = LoadFunction<PredictFunction>("TwmPredict");
                destroy = LoadFunction<DestroyFunction>("TwmDestroy");
                deviceName = LoadFunction<DeviceNameFunction>("TwmGetDeviceName");
                var error = new byte[2048];
                if (create(modelPath, shaderDirectory, validation ? 1 : 0, out context, error, (uint)error.Length) != 0 || context == IntPtr.Zero)
                    throw new InvalidOperationException("Vulkan ML initialization failed: " + Decode(error));
                var name = new byte[512];
                if (deviceName(context, name, (uint)name.Length) != 0) throw new InvalidOperationException("Cannot query the Vulkan device.");
                DeviceName = Decode(name);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private T LoadFunction<T>(string name) where T : class
        {
            IntPtr address = GetProcAddress(library, name);
            if (address == IntPtr.Zero) throw new EntryPointNotFoundException("Vulkan runtime does not export " + name);
            return Marshal.GetDelegateForFunctionPointer(address, typeof(T)) as T;
        }

        private static string Decode(byte[] value)
        {
            int end = Array.IndexOf(value, (byte)0);
            return Encoding.UTF8.GetString(value, 0, end < 0 ? value.Length : end);
        }

        public float Predict(Bitmap capture)
        {
            return PredictTensor(ImageToTensor(capture));
        }

        public float PredictTensor(float[] tensor)
        {
            if (tensor == null || tensor.Length != InputWidth * InputHeight) throw new ArgumentException("Expected a 96x96 grayscale tensor.");
            lock (sync)
            {
                if (context == IntPtr.Zero) throw new ObjectDisposedException(nameof(VulkanLoadingClassifier));
                var error = new byte[2048];
                float confidence;
                if (predict(context, tensor, (uint)tensor.Length, out confidence, error, (uint)error.Length) != 0)
                    throw new InvalidOperationException("Vulkan ML inference failed: " + Decode(error));
                if (float.IsNaN(confidence) || float.IsInfinity(confidence) || confidence < 0 || confidence > 1)
                    throw new InvalidOperationException("Vulkan ML returned an invalid confidence.");
                return confidence;
            }
        }

        // Pixel-center bilinear RGB -> floating luminance. This matches ml/model.py.
        public static float[] ImageToTensor(Bitmap capture)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            using (var source = capture.Clone(new Rectangle(0, 0, capture.Width, capture.Height), PixelFormat.Format32bppArgb))
            {
                int rowBytes = source.Width * 4;
                byte[] pixels = new byte[rowBytes * source.Height];
                var data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (int y = 0; y < source.Height; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
                }
                finally { source.UnlockBits(data); }
                var tensor = new float[InputWidth * InputHeight];
                for (int y = 0; y < InputHeight; y++)
                {
                    double sy = Math.Max(0, Math.Min(source.Height - 1, (y + 0.5) * source.Height / InputHeight - 0.5));
                    int y0 = (int)Math.Floor(sy), y1 = Math.Min(y0 + 1, source.Height - 1);
                    double fy = sy - y0;
                    for (int x = 0; x < InputWidth; x++)
                    {
                        double sx = Math.Max(0, Math.Min(source.Width - 1, (x + 0.5) * source.Width / InputWidth - 0.5));
                        int x0 = (int)Math.Floor(sx), x1 = Math.Min(x0 + 1, source.Width - 1);
                        double fx = sx - x0;
                        double luminance = 0;
                        for (int channel = 0; channel < 3; channel++)
                        {
                            double top = pixels[y0 * rowBytes + x0 * 4 + channel] * (1 - fx) + pixels[y0 * rowBytes + x1 * 4 + channel] * fx;
                            double bottom = pixels[y1 * rowBytes + x0 * 4 + channel] * (1 - fx) + pixels[y1 * rowBytes + x1 * 4 + channel] * fx;
                            double coefficient = channel == 0 ? 0.114 : channel == 1 ? 0.587 : 0.299;
                            luminance += (top * (1 - fy) + bottom * fy) * coefficient;
                        }
                        tensor[y * InputWidth + x] = (float)(luminance / 255.0);
                    }
                }
                return tensor;
            }
        }

        public void Dispose()
        {
            IntPtr retiredContext;
            IntPtr retiredLibrary;
            DestroyFunction retiredDestroy;
            lock (sync)
            {
                retiredContext = context;
                retiredLibrary = library;
                retiredDestroy = destroy;
                context = IntPtr.Zero;
                library = IntPtr.Zero;
            }
            // A timed-out GPU may still reference every resource. Keep its DLL and
            // delegate alive until native device-idle cleanup completes, away from
            // the timer/UI thread. Never free resources still used by the GPU.
            if (retiredLibrary != IntPtr.Zero)
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { if (retiredContext != IntPtr.Zero) retiredDestroy(retiredContext); }
                    finally { FreeLibrary(retiredLibrary); }
                });
            GC.SuppressFinalize(this);
        }

        ~VulkanLoadingClassifier() { Dispose(); }
    }
}
