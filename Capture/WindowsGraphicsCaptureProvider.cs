using System;
using System.Drawing;

namespace CrashTWoCLoadDetector
{
    public enum WindowsCaptureTargetKind { Window = 1, Monitor = 2 }

    public struct WindowsCaptureTarget : IEquatable<WindowsCaptureTarget>
    {
        private readonly WindowsCaptureTargetKind kind;
        private readonly IntPtr handle;

        public WindowsCaptureTarget(WindowsCaptureTargetKind kind, IntPtr handle)
        {
            if (kind != WindowsCaptureTargetKind.Window && kind != WindowsCaptureTargetKind.Monitor)
                throw new ArgumentOutOfRangeException("kind");
            if (handle == IntPtr.Zero) throw new ArgumentException("Select a capture window or monitor.", "handle");
            this.kind = kind;
            this.handle = handle;
        }

        public WindowsCaptureTargetKind Kind { get { return kind; } }
        public IntPtr Handle { get { return handle; } }
        public bool Equals(WindowsCaptureTarget other) { return kind == other.kind && handle == other.handle; }
        public override bool Equals(object obj) { return obj is WindowsCaptureTarget && Equals((WindowsCaptureTarget)obj); }
        public override int GetHashCode() { return ((int)kind * 397) ^ handle.GetHashCode(); }
        public static bool operator ==(WindowsCaptureTarget left, WindowsCaptureTarget right) { return left.Equals(right); }
        public static bool operator !=(WindowsCaptureTarget left, WindowsCaptureTarget right) { return !left.Equals(right); }
    }

    public class CaptureUnavailableException : InvalidOperationException
    {
        public CaptureUnavailableException(string message) : base(message) { }
        public CaptureUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    }

    public sealed class CaptureNotReadyException : CaptureUnavailableException
    {
        public CaptureNotReadyException(string message) : base(message) { }
    }

    public interface IWindowsCaptureSession : IDisposable
    {
        /// <summary>Returns an owned, physical-pixel client/window or full-monitor bitmap.</summary>
        Bitmap Capture();
    }

    /// <summary>Keeps one capture session alive. Native code is loaded only on the first Capture call.</summary>
    public sealed class WindowsGraphicsCaptureProvider : IDisposable
    {
        private readonly object sync = new object();
        private readonly Func<WindowsCaptureTarget, IWindowsCaptureSession> createSession;
        private IWindowsCaptureSession session;
        private WindowsCaptureTarget target;
        private bool disposed;

        public WindowsGraphicsCaptureProvider() : this(value => new NativeWindowsCaptureSession(value)) { }

        public WindowsGraphicsCaptureProvider(Func<WindowsCaptureTarget, IWindowsCaptureSession> createSession)
        {
            if (createSession == null) throw new ArgumentNullException("createSession");
            this.createSession = createSession;
        }

        public Bitmap Capture(WindowsCaptureTarget captureTarget)
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("WindowsGraphicsCaptureProvider");
                if (captureTarget.Handle == IntPtr.Zero ||
                    (captureTarget.Kind != WindowsCaptureTargetKind.Window && captureTarget.Kind != WindowsCaptureTargetKind.Monitor))
                    throw new CaptureUnavailableException("Select a capture window or monitor.");
                if (session == null || target != captureTarget)
                {
                    ResetSession();
                    session = createSession(captureTarget);
                    if (session == null) throw new CaptureUnavailableException("Cannot create the capture session.");
                    target = captureTarget;
                }
                return session.Capture();
            }
        }

        public void Reset()
        {
            lock (sync) { ResetSession(); }
        }

        private void ResetSession()
        {
            IWindowsCaptureSession previous = session;
            session = null;
            if (previous != null) previous.Dispose();
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                ResetSession();
            }
        }
    }
}
