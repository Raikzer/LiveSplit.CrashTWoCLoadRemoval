using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace LiveSplit.CrashTWoCLoadRemover.Training
{
    /// <summary>Owns accepted bitmaps until the single background writer saves or disposes them.</summary>
    public sealed class TrainingRecorder : IDisposable
    {
        private readonly object sync = new object();
        private readonly Queue<Bitmap> queue = new Queue<Bitmap>();
        private readonly TrainingSession session;
        private readonly int capacity;
        private readonly Task writer;
        private readonly IDisposable captureLease;
        private int accepted;
        private bool stopping;
        private string error;

        public TrainingRecorder(TrainingSession session, int capacity = 8)
        {
            if (session == null) throw new ArgumentNullException("session");
            if (capacity < 1) throw new ArgumentOutOfRangeException("capacity");
            this.session = session;
            this.capacity = capacity;
            accepted = session.FrameCount;
            captureLease = session.BeginRecording();
            writer = Task.Run((Action)WriteFrames);
        }

        public bool CanAccept
        {
            get { lock (sync) return !stopping && error == null && accepted < session.TargetFrames && queue.Count < capacity; }
        }
        public int AcceptedFrames { get { lock (sync) return accepted; } }
        public int SavedFrames { get { return session.FrameCount; } }
        public string Error { get { lock (sync) return error; } }

        public bool TryEnqueue(Bitmap frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (frame.Width != 100 || frame.Height != 100)
                throw new ArgumentException("Capture must be an available 100 x 100 ROI.", "frame");
            lock (sync)
            {
                if (stopping || error != null || accepted >= session.TargetFrames || queue.Count >= capacity) return false;
                queue.Enqueue(frame);
                accepted++;
                Monitor.PulseAll(sync);
                return true;
            }
        }

        public Task StopAsync()
        {
            lock (sync)
            {
                stopping = true;
                Monitor.PulseAll(sync);
                return writer;
            }
        }

        public void Dispose()
        {
            // The task retains this recorder and its lease until every accepted bitmap is handled.
            // Closing a form must not synchronously block the UI behind disk I/O.
            StopAsync();
        }

        private void WriteFrames()
        {
            try
            {
                while (true)
                {
                    Bitmap frame;
                    lock (sync)
                    {
                        while (queue.Count == 0 && !stopping && accepted < session.TargetFrames) Monitor.Wait(sync);
                        if (queue.Count == 0) break;
                        frame = queue.Dequeue();
                    }
                    try { session.AddFrame(frame); }
                    finally { frame.Dispose(); }
                }
            }
            catch (Exception exception)
            {
                lock (sync)
                {
                    stopping = true;
                    error = "Capture write failed: " + exception.Message;
                    int unsaved = accepted - session.FrameCount;
                    if (unsaved > 0) error += " " + unsaved + " accepted frame(s) could not be saved; completed PNGs were preserved.";
                    while (queue.Count > 0) queue.Dequeue().Dispose();
                }
            }
            finally
            {
                lock (sync) stopping = true;
                try { session.FinishCapture(Error); }
                catch (Exception exception)
                {
                    lock (sync) error = (error == null ? String.Empty : error + " ") + "Could not save capture status: " + exception.Message;
                }
                finally
                {
                    session.EndRecording(Error);
                    captureLease.Dispose();
                }
            }
        }
    }
}
