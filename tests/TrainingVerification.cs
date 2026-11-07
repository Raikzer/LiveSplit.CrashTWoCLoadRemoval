using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;
using CrashTWoCLoadDetector;
using LiveSplit.CrashTWoCLoadRemover;
using LiveSplit.CrashTWoCLoadRemover.Training;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options;
using LiveSplit.Options.SettingsFactories;
using LiveSplit.UI;
using LiveSplit.UI.Components;

// All image sources are synthetic. Windows are offscreen; no SendInput, desktop
// screenshots, game process, Vulkan device, or production screen capture is used.
public static class TrainingVerification
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static int skipped;
    static bool requireKeyboardFocus;
    static string root;
    static object Get(object obj, string name) { return obj.GetType().GetField(name, Flags).GetValue(obj); }
    static void Set(object obj, string name, object value) { obj.GetType().GetField(name, Flags).SetValue(obj, value); }
    static object Call(object obj, string name, params object[] args) {
        try { return obj.GetType().GetMethod(name, Flags).Invoke(obj, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static void Check(bool result, string message) {
        if (!result) throw new Exception("FAIL: " + message);
        checks++; Console.WriteLine("PASS: " + message);
    }
    static void Throws<T>(Action action, string message) where T : Exception {
        try { action(); } catch (T) { Check(true, message); return; }
        throw new Exception("FAIL: " + message + " (no " + typeof(T).Name + ")");
    }
    static Bitmap Frame() {
        var bitmap = new Bitmap(100, 100);
        using (var g = Graphics.FromImage(bitmap)) { g.Clear(Color.DarkBlue); g.FillRectangle(Brushes.White, 20, 45, 60, 10); }
        return bitmap;
    }
    static TrainingSession Session(int count, int target = 0) {
        var session = TrainingSession.Create(root, target == 0 ? Math.Max(1, count) : target, 30, "<Settings><platform>ENG/PS2</platform></Settings>");
        using (var frame = Frame()) for (int i = 0; i < count; i++) session.AddFrame(frame);
        session.FinishCapture(null); return session;
    }
    static void Wait(Func<bool> done, string message) {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.ElapsedMilliseconds < 10000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(done(), message);
    }
    static void ShowOffscreen(Form form) {
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000); form.Show(); form.Activate(); Application.DoEvents();
    }
    static TrainingCaptureForm ReviewForm(TrainingSession session) {
        var form = new TrainingCaptureForm(Frame, root, "<Settings/>");
        Set(form, "_session", session); Call(form, "RefreshReview"); return form;
    }
    static bool Key(TrainingCaptureForm form, Keys key, bool repeated = false) {
        var message = Message.Create(form.Handle, 0x100, (IntPtr)(int)key, repeated ? (IntPtr)(1L << 30) : IntPtr.Zero);
        return (bool)Call(form, "ProcessCmdKey", message, key);
    }
    static bool FocusReview(TrainingCaptureForm form) {
        var watch = Stopwatch.StartNew();
        do {
            form.Activate(); ((Button)Get(form, "_loading")).Focus(); Application.DoEvents();
            if (form.ContainsFocus && Form.ActiveForm == form) return true;
            Thread.Sleep(10);
        } while (watch.ElapsedMilliseconds < 500);
        if (requireKeyboardFocus) throw new Exception("FAIL: Windows did not grant foreground focus to the offscreen review fixture");
        skipped++; Console.WriteLine("SKIP: focused keyboard integration (Windows denied foreground activation); checking review commands directly"); return false;
    }
    static bool ReviewKey(TrainingCaptureForm form, Keys key, bool focused) {
        if (focused) return Key(form, key);
        Call(form, "ApplyReviewKey", key); return true;
    }
    static void StartManual(TrainingCaptureForm form, int target) {
        Check(SynchronizationContext.Current is WindowsFormsSynchronizationContext, "capture fixture has UI synchronization context");
        ((NumericUpDown)Get(form, "_target")).Value = target;
        Call(form, "StartCapture"); ((System.Windows.Forms.Timer)Get(form, "_captureTimer")).Stop();
        ((System.Windows.Forms.Timer)Get(form, "_progressTimer")).Stop();
    }
    static void Tick(TrainingCaptureForm form) { Set(form, "_nextCaptureMilliseconds", 0.0); Call(form, "CaptureTick", null, EventArgs.Empty); }

    static void Persistence() {
        Throws<ArgumentOutOfRangeException>(() => TrainingSession.Create(root, 0, 30, null), "zero target rejected");
        Throws<ArgumentOutOfRangeException>(() => TrainingSession.Create(root, 1, 31, null), "rate above 30 FPS rejected");
        var session = Session(105);
        Check(session.Complete && session.FrameCount == 105, "exact target is durable and complete");
        Check(XElement.Load(Path.Combine(session.DirectoryPath, "session.xml")).Element("captureSettingsXml").Value.Contains("ENG/PS2"), "capture settings snapshot stored as XML text");
        session.LabelCurrent(true); session.LabelCurrent(false); session.Previous();
        Check(session.Cursor == 1 && session.GetLabel(1) == 0, "R moves backward without erasing a label");
        session.LabelCurrent(true); session.LabelNextAbsent(100);
        Check(session.Cursor == 102 && session.GetLabel(1) == 1 && session.GetLabel(2) == 0 && session.GetLabel(101) == 0 && session.GetLabel(102) == null, "U labels exactly current plus 99 and advances 100");
        session.LabelNextAbsent(100);
        Check(session.Cursor == 105 && session.LabeledCount == 105, "U clamps to remaining frames");
        var reopened = TrainingSession.Open(session.DirectoryPath);
        Check(reopened.Cursor == 105 && reopened.GetLabel(0) == 1 && reopened.GetLabel(1) == 1 && reopened.GetLabel(104) == 0, "reopen restores labels and cursor");
        reopened.Previous(); reopened.LabelCurrent(true);
        Check(TrainingSession.Open(session.DirectoryPath).GetLabel(104) == 1, "relabel after reopen is durable");
        var sparse = Session(5, 10); sparse.Seek(3); sparse.LabelCurrent(true);
        string[] csv = File.ReadAllLines(sparse.ExportManifest());
        Check(csv.Length == 2 && csv[0] == "path,label,group" && csv[1].StartsWith("frames/frame000003.png,1,"), "export contains only labeled PNG paths with group");
        sparse.Previous(); sparse.LabelCurrent(false);
        Check(File.ReadAllLines(sparse.ExportManifest())[1].StartsWith("frames/frame000003.png,0,"), "re-export replaces previous labels");
        string journal = Path.Combine(sparse.DirectoryPath, "review.journal");
        File.AppendAllText(journal, "interrupted transaction");
        var recovered = TrainingSession.Open(sparse.DirectoryPath);
        Check(recovered.Cursor == 4 && recovered.GetLabel(3) == 0 && recovered.CaptureError.Contains("interrupted"), "torn final transaction recovers last durable review state");
        recovered.LabelCurrent(true);
        Check(TrainingSession.Open(sparse.DirectoryPath).Cursor == 5, "journal remains writable after recovery");
        var stale = TrainingSession.Open(session.DirectoryPath); reopened.Previous();
        Throws<IOException>(() => stale.Previous(), "stale review writer fails instead of overwriting journal");
        var broken = Session(1); File.AppendAllText(Path.Combine(broken.DirectoryPath, "review.journal"), "corrupt complete line\n");
        Throws<InvalidDataException>(() => TrainingSession.Open(broken.DirectoryPath), "complete corrupt journal transaction rejected");
        var missing = Session(2); File.Delete(missing.GetFramePath(0));
        Throws<InvalidDataException>(() => TrainingSession.Open(missing.DirectoryPath), "missing frame sequence rejected on open");
        var invalidImage = Session(1); File.WriteAllText(invalidImage.GetFramePath(0), "not a PNG");
        Throws<ArgumentException>(() => TrainingSession.Open(invalidImage.DirectoryPath), "corrupt saved image rejected on open");
        var temporary = Session(1); File.WriteAllText(Path.Combine(temporary.DirectoryPath, "frames", "ignored.tmp"), "partial PNG");
        Check(TrainingSession.Open(temporary.DirectoryPath).FrameCount == 1, "unfinished temporary PNG is ignored");
        using (var frame = Frame()) Throws<InvalidOperationException>(() => session.AddFrame(frame), "frames cannot exceed capture target");
    }

    static void LongPaths() {
        // Match a deeply nested portable LiveSplit installation while remaining
        // within the Windows legacy path limit for the final PNG itself.
        string longRoot = Path.Combine(root, "portable-" + new string('p', Math.Max(1, 165 - root.Length - 10)));
        var session = TrainingSession.Create(longRoot, 1, 30, "<Settings/>");
        using (var frame = Frame()) session.AddFrame(frame);
        session.FinishCapture(null); session.LabelCurrent(true);
        Check(TrainingSession.Open(session.DirectoryPath).Complete && File.ReadAllLines(session.ExportManifest()).Length == 2, "165-character session root supports create/capture/reopen/export");
    }

    static void Recorder() {
        var session = Session(0, 3); var recorder = new TrainingRecorder(session, 1);
        Throws<IOException>(() => new TrainingRecorder(TrainingSession.Open(session.DirectoryPath)), "second capture writer cannot open session lease");
        Bitmap first = Frame(), second = Frame(), rejected = Frame(); Task stop;
        lock (Get(session, "sync")) {
            Check(recorder.TryEnqueue(first), "first synthetic frame accepted");
            var watch = Stopwatch.StartNew();
            var queue = (System.Collections.ICollection)Get(recorder, "queue");
            while (queue.Count != 0 && watch.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            Check(queue.Count == 0, "writer dequeued frame and waits for session lock");
            Check(recorder.TryEnqueue(second) && !recorder.TryEnqueue(rejected) && !recorder.CanAccept, "bounded pending queue rejects excess without taking ownership");
            Check(rejected.Width == 100 && recorder.AcceptedFrames == 2, "rejected bitmap remains caller owned and uncounted");
            stop = recorder.StopAsync(); Check(!stop.IsCompleted, "StopAsync returns while queued work remains");
        }
        Check(stop.Wait(10000), "stop flushes all accepted queued frames"); rejected.Dispose();
        Check(session.FrameCount == 2 && !session.Complete && recorder.Error == null, "early stop preserves a partial capture");
        Throws<ArgumentException>(() => { int width = first.Width; }, "saved first bitmap was disposed by recorder");
        Throws<ArgumentException>(() => { int width = second.Width; }, "saved queued bitmap was disposed by recorder");
        using (var extra = Frame()) Check(!recorder.TryEnqueue(extra), "stopped writer rejects later frames");
        using (var resumed = new TrainingRecorder(TrainingSession.Open(session.DirectoryPath))) {
            var frame = Frame(); Check(resumed.TryEnqueue(frame), "released capture lease permits reopening and resuming");
            Check(resumed.StopAsync().Wait(10000) && resumed.AcceptedFrames == 3, "resumed recorder honors original target");
        }
        Check(TrainingSession.Open(session.DirectoryPath).Complete, "resumed exact-target capture reopens complete");
        recorder.Dispose(); recorder.Dispose();
        var failed = Session(0, 2); Directory.Delete(Path.Combine(failed.DirectoryPath, "frames"));
        using (var failing = new TrainingRecorder(failed)) {
            Check(failing.TryEnqueue(Frame()), "writer accepts frame before injected IO failure");
            Check(failing.StopAsync().Wait(10000) && failing.Error.Contains("Capture write failed") && !failed.Complete, "IO error completes stop with explicit failure and partial state");
        }
        var blocked = Session(0, 2);
        using (var waiting = new TrainingRecorder(blocked)) {
            Task stopped;
            lock (Get(blocked, "writeSync")) {
                Check(waiting.TryEnqueue(Frame()), "writer accepts frame before blocked IO gate");
                var readers = Task.Run(() => blocked.FrameCount + blocked.Cursor + blocked.LabeledCount + waiting.SavedFrames);
                Check(readers.Wait(1000) && readers.Result == 0, "progress getters remain responsive while writer IO gate is blocked");
                stopped = waiting.StopAsync(); Check(!stopped.IsCompleted, "blocked writer stop remains asynchronous");
            }
            Check(stopped.Wait(10000) && blocked.FrameCount == 1, "blocked writer flushes after IO gate is released");
        }
    }

    static void ReviewAndKeys() {
        var session = Session(105);
        using (var form = ReviewForm(session)) {
            Check(((NumericUpDown)Get(form, "_target")).Value == 20000 && ((NumericUpDown)Get(form, "_fps")).Value == 30, "UI defaults to 20,000 frames at 30 FPS");
            Check(((NumericUpDown)Get(form, "_fps")).Maximum == 30 && form.KeyPreview, "UI caps rate at 30 and previews keyboard input");
            ShowOffscreen(form); var loading = (Button)Get(form, "_loading"); loading.Focus(); Application.DoEvents();
            bool focused = FocusReview(form); string route = focused ? "ProcessCmdKey" : "review command";
            if (focused) Check(true, "offscreen review form owns keyboard focus");
            Check(ReviewKey(form, Keys.Y, focused) && session.Cursor == 1 && session.GetLabel(0) == 1, route + " Y labels loading and advances");
            if (focused) Check(Key(form, Keys.N, true) && session.Cursor == 1, "held-key auto-repeat is consumed without labeling");
            Check(ReviewKey(form, Keys.N, focused) && session.GetLabel(1) == 0 && session.Cursor == 2, route + " N labels absent and advances");
            Check(ReviewKey(form, Keys.R, focused) && session.Cursor == 1 && session.GetLabel(1) == 0, route + " R retains previous label");
            Check(ReviewKey(form, Keys.U, focused) && session.Cursor == 101 && session.GetLabel(100) == 0 && session.GetLabel(101) == null, route + " U labels correct 100-frame span");
            if (focused) {
                Check(!Key(form, Keys.Y | Keys.Control) && session.Cursor == 101, "modified review key does not label");
                ((NumericUpDown)Get(form, "_target")).Focus(); Application.DoEvents(); Key(form, Keys.Y);
                Check(session.Cursor == 101, "numeric editing focus blocks review hotkeys");
            }
            loading.Focus(); Set(form, "_busy", true); Call(form, "ApplyReviewKey", Keys.Y);
            Check(session.Cursor == 101, "busy operation blocks review commands"); Set(form, "_busy", false);
            Set(form, "_dialogOpen", true); Call(form, "ApplyReviewKey", Keys.Y);
            Check(session.Cursor == 101, "modal-dialog state blocks review commands"); Set(form, "_dialogOpen", false);
            using (var other = new Form()) {
                ShowOffscreen(other); Key(form, Keys.Y);
                Check(session.Cursor == 101, "inactive review form does not consume gameplay hotkeys");
            }
            focused = FocusReview(form); ReviewKey(form, Keys.U, focused);
            Check(session.Cursor == 105, "U clamps at end of session");
            ReviewKey(form, Keys.R, focused); Check(session.Cursor == 104 && ((Button)Get(form, "_loading")).Enabled, "R returns from end to last reviewable image");
            using (var unlocked = new FileStream(session.GetFramePath(104), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(true, "review preview does not retain PNG file handle");
            using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "training-review.png")); }
            File.WriteAllText(session.GetFramePath(104), "broken after open"); Call(form, "RefreshReview");
            Check(!((Button)Get(form, "_loading")).Enabled && !((Button)Get(form, "_absentBatch")).Enabled && ((Label)Get(form, "_status")).Text.Contains("Cannot read"), "unreadable current frame disables labeling and reports error");
            Call(form, "ApplyReviewKey", Keys.Y); Check(session.Cursor == 104, "unreadable frame cannot silently receive a label");
        }
    }

    static void FormCaptureWarmup() {
        int attempts = 0; var transitions = new List<bool>();
        using (var form = new TrainingCaptureForm(() => {
            attempts++;
            if (attempts <= 3) throw new CaptureNotReadyException("Synthetic WGC warmup");
            return Frame();
        }, root, "<Settings/>", transitions.Add)) {
            ShowOffscreen(form); StartManual(form, 2);
            var session = (TrainingSession)Get(form, "_session"); var recorder = (TrainingRecorder)Get(form, "_recorder");
            for (int i = 0; i < 3; i++) Tick(form);
            Check(attempts == 3 && form.IsCapturing && Get(form, "_stopTask") == null && transitions.SequenceEqual(new[] { true }),
                "Pending capture warmup keeps the recording active without starting shutdown");
            Check(recorder.AcceptedFrames == 0 && session.FrameCount == 0 && Directory.GetFiles(session.DirectoryPath, "*.png", SearchOption.AllDirectories).Length == 0,
                "Pending capture frames consume no target slots and write no placeholder samples");
            Tick(form);
            Check(attempts == 4 && recorder.AcceptedFrames == 1 && form.IsCapturing,
                "first ready synthetic frame is accepted after Pending warmup");
            Tick(form); Wait(() => !form.IsCapturing, "recording reaches target normally after warmup frames become ready");
            var restored = TrainingSession.Open(session.DirectoryPath);
            Check(attempts == 5 && session.Complete && restored.Complete && restored.FrameCount == 2 && string.IsNullOrEmpty(restored.CaptureError)
                && transitions.SequenceEqual(new[] { true, false }), "warmup recording persists exactly the ready-frame target without a capture error");
        }
    }

    static void FormCapture() {
        var transitions = new List<bool>(); int captures = 0;
        using (var form = new TrainingCaptureForm(() => { captures++; return Frame(); }, root, "<Settings/>", transitions.Add)) {
            ShowOffscreen(form); StartManual(form, 3);
            Check(form.IsCapturing && transitions.SequenceEqual(new[] { true }), "start raises capture-active state once");
            Check(!((Button)Get(form, "_loading")).Enabled && !((NumericUpDown)Get(form, "_fps")).Enabled && ((Button)Get(form, "_stop")).Enabled, "capture disables review and rate changes while enabling stop");
            ((Stopwatch)Get(form, "_elapsed")).Reset();
            Tick(form); double deadline = (double)Get(form, "_nextCaptureMilliseconds"); TickWithoutDeadlineReset(form);
            Check(captures == 1 && Math.Abs(deadline - 1000.0 / 30) < 1e-9, "30 FPS capture schedules fractional deadline and skips early tick");
            Tick(form); Tick(form); Wait(() => !form.IsCapturing, "automatic target stop finishes asynchronously");
            var session = (TrainingSession)Get(form, "_session");
            Check(captures == 3 && session.FrameCount == 3 && session.Complete && transitions.SequenceEqual(new[] { true, false }), "auto-completion saves exactly target and clears capture state");
            Check(((Button)Get(form, "_loading")).Enabled && ((Button)Get(form, "_open")).Enabled && ((Button)Get(form, "_export")).Enabled && !((Button)Get(form, "_stop")).Enabled, "completed capture enables review/open/export controls");
            // Explicitly restore foreground focus after the asynchronous writer
            // finishes; another simultaneously running fixture may own it.
            ReviewKey(form, Keys.Y, FocusReview(form));
            Check(session.Cursor == 1, "review works immediately after capture completion");
            using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "training-capture-complete.png")); }
        }
        var closed = new TrainingCaptureForm(Frame, root, "<Settings/>", transitions.Add);
        ShowOffscreen(closed); StartManual(closed, 100); Tick(closed); var partial = (TrainingSession)Get(closed, "_session"); closed.CloseSafely();
        Wait(() => closed.IsDisposed, "CloseSafely closes form after flushing accepted frames");
        Check(partial.FrameCount == 1 && TrainingSession.Open(partial.DirectoryPath).FrameCount == 1 && !partial.Complete, "close preserves reviewable partial capture");
        var direct = new TrainingCaptureForm(Frame, root, "<Settings/>", transitions.Add);
        ShowOffscreen(direct); StartManual(direct, 100); Tick(direct);
        var writer = (TrainingRecorder)Get(direct, "_recorder"); direct.Dispose();
        Check(writer.StopAsync().Wait(10000) && !direct.IsCapturing && Get(direct, "_capture") == null, "direct disposal flushes writer and releases capture delegate");
        var failure = new TrainingCaptureForm(() => { throw new IOException("synthetic capture failure"); }, root, "<Settings/>");
        ShowOffscreen(failure); StartManual(failure, 10); var failed = (TrainingSession)Get(failure, "_session");
        // Request close before injected failure so the production error path cannot open a modal dialog.
        Set(failure, "_closeRequested", true); Tick(failure);
        Wait(() => failure.IsDisposed, "capture delegate failure releases form during owner shutdown");
        Check(failed.CaptureError.Contains("synthetic capture failure") && !failed.Complete, "capture delegate error is durable in partial session");
    }
    static void TickWithoutDeadlineReset(TrainingCaptureForm form) { Call(form, "CaptureTick", null, EventArgs.Empty); }

    static LiveSplitState State() {
        var run = new Run(new StandardComparisonGeneratorsFactory()); run.Add(new Segment("Finish"));
        return new LiveSplitState(run, null, null, new LiveSplit.Options.LayoutSettings(), new StandardSettingsFactory().Create());
    }
    static void ComponentIntegration() {
        var state = State(); Type type = typeof(CrashTWoCLoadRemovalFactory).Assembly.GetType("LiveSplit.UI.Components.CrashTWoCLoadRemovalComponent", true);
        var component = (IComponent)Activator.CreateInstance(type, new object[] { state, false });
        var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
        try {
            var tabs = (TabControl)Get(settings, "tabControl1");
            Check(tabs.TabPages.Cast<TabPage>().Any(page => page.Text == "Training"), "settings expose Training tab");
            using (var host = new Form { ClientSize = settings.Size }) {
                host.Controls.Add(settings); ShowOffscreen(host);
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Training"); Application.DoEvents();
                using (var image = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(image, settings.ClientRectangle); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "training-settings.png")); }
                host.Controls.Remove(settings);
            }
            Set(settings, "platform", "JPN/PS2"); Call(settings, "OpenTrainingMode");
            Check(Get(settings, "trainingForm") == null && ((Label)Get(settings, "trainingModeStatus")).Text.Contains("Select ENG/PS2"), "unsupported platform explains training prerequisite without opening capture");
            foreach (string name in new[] { "isLoading", "isBlackScreen", "wasBlackScreen", "waitOnFadeIn", "waitOnLoad", "specialLoad", "isCmpFinished" }) Set(component, name, true);
            Set(component, "waitFrames", 123); Set(component, "lowestBit", 25); state.IsGameTimePaused = true;
            Call(settings, "SetTrainingCaptureActive", true);
            Check(!state.IsGameTimePaused && !(bool)Get(component, "isLoading") && !(bool)Get(component, "waitOnLoad") && (int)Get(component, "waitFrames") == 0, "training start resets recognition transitions and resumes game time");
            Set(settings, "processCaptureIndex", int.MaxValue); Set(settings, "processList", new Process[0]);
            Set(component, "recognitionKey", "sentinel"); Set(component, "waitFrames", 77);
            component.Update(null, state, 0, 0, LayoutMode.Vertical);
            Check((string)Get(component, "recognitionKey") == "sentinel" && (int)Get(component, "waitFrames") == 77, "component Update skips capture and recognition while training is active");
            Call(settings, "SetTrainingCaptureActive", false);
            Check((int)Get(component, "waitFrames") == 0 && !settings.TrainingCaptureActive, "training stop resets stale recognition state");
            var training = new TrainingCaptureForm(Frame, root, "<Settings/>"); Set(settings, "trainingForm", training);
            component.Dispose();
            Check(training.IsDisposed && !settings.TrainingCaptureActive, "component disposal closes owned idle training form");
        } finally { component.Dispose(); settings.Dispose(); }
    }

    [STAThread] public static int Main(string[] args) {
        requireKeyboardFocus = args.Contains("--require-keyboard-focus");
        root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            ComponentManager.RaceProviderFactories = new Dictionary<string, IRaceProviderFactory>();
            int result = 1;
            using (var messageHost = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) }) {
                // Run the real UI message loop so asynchronous form continuations
                // use the same synchronization context as their LiveSplit host.
                messageHost.Shown += delegate {
                    messageHost.BeginInvoke(new Action(() => {
                        try {
                            Persistence(); Recorder(); ReviewAndKeys(); FormCaptureWarmup(); FormCapture(); ComponentIntegration(); LongPaths();
                            Console.WriteLine("RESULT: " + checks + " training checks passed; " + skipped + " focused-keyboard sections skipped; synthetic frames only; " + (IntPtr.Size * 8) + " bit");
                            result = 0;
                        } catch (Exception ex) { Console.Error.WriteLine(ex); }
                        finally { messageHost.Close(); }
                    }));
                };
                Application.Run(messageHost);
            }
            return result;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
