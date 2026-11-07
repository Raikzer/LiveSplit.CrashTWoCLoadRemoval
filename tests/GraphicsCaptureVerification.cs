using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using CrashTWoCLoadDetector;
using LiveSplit.CrashTWoCLoadRemover;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options.SettingsFactories;
using LiveSplit.UI.Components;

// Default suite: no actual capture, native WGC library, game, OCR or GPU.
public static class GraphicsCaptureVerification
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    static int checks;
    static readonly Rectangle Full = new Rectangle(0, 0, 400, 160);
    static void Check(bool result, string message) {
        if (!result) throw new Exception("FAIL: " + message);
        checks++; Console.WriteLine("PASS: " + message);
    }
    static void Throws<T>(Action action, string message) where T : Exception {
        try { action(); } catch (T) { Check(true, message); return; }
        throw new Exception("FAIL: " + message + "; expected " + typeof(T).Name);
    }
    sealed class Session : IWindowsCaptureSession {
        public int Captures, Disposals, State;
        public Bitmap Capture() {
            if (Disposals != 0) throw new Exception("Disposed session was used");
            Captures++;
            if (State == 1) throw new CaptureNotReadyException("Synthetic pending");
            if (State == 2) throw new CaptureUnavailableException("Synthetic source lost");
            var frame = new Bitmap(4, 4); frame.SetPixel(0, 0, Color.Cyan); return frame;
        }
        public void Dispose() { Disposals++; }
    }
    static WindowsCaptureTarget Target(int handle) { return new WindowsCaptureTarget(WindowsCaptureTargetKind.Window, new IntPtr(handle)); }
    static void Lifecycle() {
        Check(GetModuleHandle("TwocWindowsGraphicsCapture.dll") == IntPtr.Zero, "native WGC library starts unloaded");
        using (var lazy = new WindowsGraphicsCaptureProvider()) { lazy.Reset(); }
        Check(GetModuleHandle("TwocWindowsGraphicsCapture.dll") == IntPtr.Zero, "default provider construction/reset/disposal never loads native WGC");
        var created = new List<Session>(); var targets = new List<WindowsCaptureTarget>();
        var provider = new WindowsGraphicsCaptureProvider(target => { var value = new Session(); created.Add(value); targets.Add(target); return value; });
        provider.Reset(); provider.Reset();
        using (var frame = provider.Capture(Target(7))) Check(frame.GetPixel(0, 0).ToArgb() == Color.Cyan.ToArgb(), "provider returns the session's owned frame");
        using (var frame = provider.Capture(Target(7))) { }
        Check(created.Count == 1 && created[0].Captures == 2, "same HWND reuses one session across frames");
        created[0].State = 1;
        Throws<CaptureNotReadyException>(() => provider.Capture(Target(7)), "Pending propagates instead of returning an earlier frame");
        created[0].State = 2;
        Throws<CaptureUnavailableException>(() => provider.Capture(Target(7)), "source loss propagates instead of returning an earlier frame");
        created[0].State = 0;
        using (var frame = provider.Capture(Target(7))) { }
        Check(created.Count == 1 && created[0].Captures == 5, "pending/unavailable do not churn the session and a later frame recovers");
        using (var frame = provider.Capture(Target(8))) { }
        Check(created.Count == 2 && created[0].Disposals == 1 && created[1].Disposals == 0, "changed HWND disposes previous session once");
        // Fake factory only: no monitor/native API is invoked by this target-kind test.
        using (var frame = provider.Capture(new WindowsCaptureTarget(WindowsCaptureTargetKind.Monitor, new IntPtr(8)))) { }
        Check(created.Count == 3 && created[1].Disposals == 1 && targets[2].Kind == WindowsCaptureTargetKind.Monitor, "source kind participates in session identity");
        provider.Reset(); provider.Reset();
        Check(created[2].Disposals == 1, "repeated Reset disposes the active session only once");
        using (var frame = provider.Capture(Target(8))) { }
        provider.Dispose(); provider.Dispose(); provider.Reset();
        Check(created.Count == 4 && created[3].Disposals == 1, "Reset recreates on next capture and repeated Dispose is safe");
        Throws<ObjectDisposedException>(() => provider.Capture(Target(8)), "disposed provider cannot create a new session");
        int attempts = 0;
        using (var retry = new WindowsGraphicsCaptureProvider(target => { attempts++; return attempts == 1 ? null : new Session(); })) {
            Throws<CaptureUnavailableException>(() => retry.Capture(Target(9)), "null session factory gives typed unavailable");
            using (var frame = retry.Capture(Target(9))) { }
            Check(attempts == 2, "failed session creation can be retried");
            Throws<CaptureUnavailableException>(() => retry.Capture(default(WindowsCaptureTarget)), "default zero target is rejected before session creation");
            Check(attempts == 2, "invalid default target never invokes factory");
        }
        var first = Target(0x12345678); var same = Target(0x12345678);
        Check(first == same && first.Equals((object)same) && first.GetHashCode() == same.GetHashCode(), "target equality and hashing agree");
        if (IntPtr.Size == 8) {
            var high = new WindowsCaptureTarget(WindowsCaptureTargetKind.Window, new IntPtr(0x112345678L));
            Check(high != first && high.Handle.ToInt64() == 0x112345678L, "target preserves upper 32 bits of a 64-bit HWND");
            using (var wide = new WindowsGraphicsCaptureProvider(target => { Check(target.Handle == high.Handle, "factory receives full-width HWND"); return new Session(); }))
                using (var frame = wide.Capture(high)) { }
        }
        Throws<ArgumentException>(() => new WindowsCaptureTarget(WindowsCaptureTargetKind.Window, IntPtr.Zero), "explicit zero HWND is rejected");
        Throws<ArgumentOutOfRangeException>(() => new WindowsCaptureTarget((WindowsCaptureTargetKind)99, new IntPtr(7)), "unknown source kind is rejected");
    }
    static ImageCaptureInfo Info() {
        return new ImageCaptureInfo { featureVectorResolutionX = 640, featureVectorResolutionY = 480,
            captureSizeX = 100, captureSizeY = 50, captureAspectRatio = 4f / 3,
            resizeSizeXEng = 80, resizeSizeYEng = 20, resizeSizeXJpn = 90, resizeSizeYJpn = 24, resizeState = ResizeState.ENG };
    }
    static Bitmap SplitFrame(int width, int height) {
        var image = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(image)) {
            graphics.Clear(Color.Red); graphics.FillRectangle(Brushes.Blue, width / 2, 0, width - width / 2, height);
        }
        return image;
    }
    static bool ColorAt(Bitmap image, int x, int y, Color color) {
        Color pixel = image.GetPixel(x, y);
        return Math.Abs(pixel.R - color.R) <= 2 && Math.Abs(pixel.G - color.G) <= 2 && Math.Abs(pixel.B - color.B) <= 2;
    }
    static void Geometry() {
        var info = Info();
        using (var frame = SplitFrame(801, 601)) {
            using (var preview = CapturedFrameProcessor.Process(frame, ref info, Full, CapturedFramePurpose.FullPreview)) {
                Check(preview.Width == 100 && preview.Height == 50 && ColorAt(preview, 10, 25, Color.Red) && ColorAt(preview, 90, 25, Color.Blue), "full preview resizes the full captured client into configured preview dimensions");
                Check(info.crop_coordinate_right == 800 && info.crop_coordinate_bottom == 600, "odd client dimensions use the same even preview bounds as GDI");
            }
            using (var selected = CapturedFrameProcessor.Process(frame, ref info, new Rectangle(200, 0, 200, 160), CapturedFramePurpose.CroppedPreview)) {
                Check(selected.Width == 100 && selected.Height == 50 && ColorAt(selected, 10, 25, Color.Blue) && ColorAt(selected, 90, 25, Color.Blue), "400x160 selection coordinates map into the right physical client half");
                Check(info.crop_coordinate_left == 400 && info.crop_coordinate_right == 800 && info.crop_coordinate_bottom == 600, "selection coordinates are recomputed from current physical frame");
            }
            using (var detector = CapturedFrameProcessor.Process(frame, ref info, new Rectangle(0, 0, 200, 80), CapturedFramePurpose.Detector))
                Check(detector.Width == 80 && detector.Height == 20 && ColorAt(detector, 40, 10, Color.Red), "English detector crops relative to selected region and uses language output dimensions");
            info.resizeState = ResizeState.JPN;
            using (var detector = CapturedFrameProcessor.Process(frame, ref info, new Rectangle(200, 0, 200, 160), CapturedFramePurpose.Detector))
                Check(detector.Width == 90 && detector.Height == 24 && ColorAt(detector, 45, 12, Color.Blue), "Japanese detector uses its output dimensions and current selected-region center");
            Check(frame.Width == 801 && frame.Height == 601 && ColorAt(frame, 0, 0, Color.Red), "processing leaves caller-owned full frame usable and unchanged");
            foreach (var bad in new[] { Rectangle.Empty, new Rectangle(-1, 0, 1, 1), new Rectangle(399, 0, 2, 1), new Rectangle(0, 159, 1, 2), new Rectangle(int.MaxValue, 0, int.MaxValue, 1) }) {
                var invalid = bad;
                Throws<CaptureUnavailableException>(() => CapturedFrameProcessor.Process(frame, ref info, invalid, CapturedFramePurpose.Detector), "invalid/out-of-bounds selection rejected: " + bad);
            }
            info = Info(); info.cropOffsetX = 10000;
            Throws<CaptureUnavailableException>(() => CapturedFrameProcessor.Process(frame, ref info, Full, CapturedFramePurpose.Detector), "out-of-client detector offset fails instead of sampling unrelated pixels");
            info = Info(); info.featureVectorResolutionY = float.NaN;
            Throws<CaptureUnavailableException>(() => CapturedFrameProcessor.Process(frame, ref info, Full, CapturedFramePurpose.Detector), "non-finite geometry is rejected");
        }
        foreach (var size in new[] { new Size(800, 600), new Size(1600, 1200), new Size(1200, 600) }) {
            using (var frame = SplitFrame(size.Width, size.Height)) {
                info = Info();
                using (var detector = CapturedFrameProcessor.Process(frame, ref info, Full, CapturedFramePurpose.Detector)) {
                    float factor = size.Height / 600f;
                    Check(Math.Abs(info.actual_crop_size_x - 125 * factor) < .001 && Math.Abs(info.actual_crop_size_y - 62.5f * factor) < .001,
                        "detector ROI follows GDI height/aspect scaling at physical size " + size);
                    Check(detector.Width == 80 && detector.Height == 20 && ColorAt(detector, 5, 10, Color.Red) && ColorAt(detector, 75, 10, Color.Blue), "DPI/aspect scaling keeps equivalent normalized detector pixels at " + size);
                }
            }
        }
        using (var tiny = new Bitmap(1, 1)) { info = Info(); Throws<CaptureUnavailableException>(() => CapturedFrameProcessor.Process(tiny, ref info, Full, CapturedFramePurpose.FullPreview), "empty even-normalized client is safely unavailable"); }
    }
    static object StaticNative(string method, params object[] args) {
        var type = typeof(WindowsGraphicsCaptureProvider).Assembly.GetType("CrashTWoCLoadDetector.NativeWindowsCaptureSession", true);
        try { return type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static void NativeMarshalling() {
        var pending = (CaptureNotReadyException)StaticNative("NotReady", Encoding.UTF8.GetBytes("Synthetic resize — várakozás\0ignored"));
        Check(pending.Message == "Synthetic resize — várakozás", "native Pending preserves its UTF-8 diagnostic and excludes bytes after NUL");
        pending = (CaptureNotReadyException)StaticNative("NotReady", new byte[4]);
        Check(!string.IsNullOrEmpty(pending.Message), "empty native Pending diagnostic has a useful fallback");
        Check((int)StaticNative("ValidateFrame", 3u, 2u, 12u) == 24, "packed odd-width frame validation returns exact byte count");
        Throws<CaptureUnavailableException>(() => StaticNative("ValidateFrame", 0u, 2u, 0u), "zero native frame extent rejected before allocation");
        Throws<CaptureUnavailableException>(() => StaticNative("ValidateFrame", 3u, 2u, 16u), "non-packed native stride rejected before bitmap copy");
        Throws<CaptureUnavailableException>(() => StaticNative("ValidateFrame", 65536u, 65536u, 262144u), "excessive native frame allocation rejected");
        var pixels = new byte[8 * 6 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 34; pixels[i + 1] = 73; pixels[i + 2] = 191; pixels[i + 3] = 101; }
        using (var frame = (Bitmap)StaticNative("CreateBitmap", pixels, 8, 6, 32)) {
            Check(ColorAt(frame, 4, 3, Color.FromArgb(191, 73, 34)) && frame.GetPixel(4, 3).A == 255, "native BGRA conversion preserves RGB while treating DWM alpha as opaque");
            var info = Info();
            using (var resized = CapturedFrameProcessor.Process(frame, ref info, Full, CapturedFramePurpose.FullPreview))
                Check(ColorAt(resized, 50, 25, Color.FromArgb(191, 73, 34)), "downstream preview resizing preserves RGB with nonopaque native source alpha");
        }
    }
    static readonly BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static object Field(object value, string name) { return value.GetType().GetField(name, InstanceFlags).GetValue(value); }
    static void SetField(object value, string name, object field) { value.GetType().GetField(name, InstanceFlags).SetValue(value, field); }
    static object Call(object value, string method, params object[] args) {
        try { return value.GetType().GetMethod(method, InstanceFlags).Invoke(value, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    static void Load(CrashTWoCLoadRemovalSettings settings, string xml) {
        var doc = new XmlDocument(); doc.LoadXml(xml); settings.SetSettings(doc.DocumentElement);
    }
    static void Settings() {
        ComponentManager.RaceProviderFactories = new Dictionary<string, IRaceProviderFactory>();
        var run = new Run(new StandardComparisonGeneratorsFactory()); run.GameName = "Synthetic verification"; run.CategoryName = "WGC settings"; run.Add(new Segment("Finish"));
        var state = new LiveSplitState(run, null, null, new LiveSplit.Options.LayoutSettings(), new StandardSettingsFactory().Create());
        using (var settings = new CrashTWoCLoadRemovalSettings(state, false)) {
            var controls = settings.Controls.Find("UseWindowsGraphicsCapture", true);
            Check(controls.Length == 1 && controls[0] is CheckBox, "Setup contains one Windows Graphics Capture checkbox");
            var checkbox = (CheckBox)controls[0]; var scaling = (TrackBar)Field(settings, "trackBar1");
            Check(checkbox.Text == "Use Windows Graphics Capture" && !checkbox.Checked && !settings.UseWindowsGraphicsCapture && scaling.Enabled, "new settings default to GDI with manual scaling available");
            var saved = settings.GetSettings(new XmlDocument());
            Check(saved["UseWindowsGraphicsCapture"].InnerText == "False", "default capture backend is saved explicitly");
            int changes = 0, batchProbes = 0;
            settings.DetectionConfigurationChanged += (sender, args) => { Check(!(bool)Field(settings, "applyingCaptureSettings"), "configuration event is emitted only after settings batch completes"); changes++; };
            checkbox.CheckedChanged += (sender, args) => {
                if (!(bool)Field(settings, "applyingCaptureSettings")) return;
                batchProbes++;
                Throws<CaptureNotReadyException>(() => settings.CaptureImage(), "capture requested during XML backend change is Pending before source resolution");
            };
            Load(settings, "<Settings><UseWindowsGraphicsCapture>true</UseWindowsGraphicsCapture><ScalingPercent>150</ScalingPercent><CaptureRegion><X>20</X><Y>10</Y><Width>300</Width><Height>120</Height></CaptureRegion></Settings>");
            Check(settings.UseWindowsGraphicsCapture && checkbox.Checked && !scaling.Enabled && scaling.Value == 150, "enabled XML selects WGC and preserves the stored manual scaling value");
            Check(changes == 1 && batchProbes == 1 && Field(settings, "graphicsCaptureProvider") == null, "XML batch notifies once and capturePreview=false never creates a provider");
            saved = settings.GetSettings(new XmlDocument());
            Check(saved["UseWindowsGraphicsCapture"].InnerText == "True" && saved["ScalingPercent"].InnerText == "150", "WGC opt-in and manual scaling persist together");
            using (var restored = new CrashTWoCLoadRemovalSettings(state, false)) {
                restored.SetSettings(saved);
                Check(restored.UseWindowsGraphicsCapture && Field(restored, "graphicsCaptureProvider") == null, "saved WGC settings restore without starting capture");
            }
            foreach (string xml in new[] { "<Settings/>", "<Settings><UseWindowsGraphicsCapture>garbage</UseWindowsGraphicsCapture></Settings>", "<Settings><UseWindowsGraphicsCapture/></Settings>", "<Settings><UseWindowsGraphicsCapture>1</UseWindowsGraphicsCapture></Settings>", "<Settings><UseWindowsGraphicsCapture>false</UseWindowsGraphicsCapture></Settings>" }) {
                checkbox.Checked = true;
                Load(settings, xml);
                Check(!settings.UseWindowsGraphicsCapture && !checkbox.Checked && scaling.Enabled, "legacy/invalid/false XML safely selects GDI: " + xml);
            }
            checkbox.Checked = true;
            var session = new Session();
            var provider = new WindowsGraphicsCaptureProvider(target => session);
            using (var frame = provider.Capture(Target(7))) { }
            settings.GetType().GetField("graphicsCaptureProvider", InstanceFlags).SetValue(settings, provider);
            checkbox.Checked = false;
            Check(session.Disposals == 1 && Field(settings, "graphicsCaptureProvider") == null, "backend checkbox change releases the active session");
            Check(GetModuleHandle("TwocWindowsGraphicsCapture.dll") == IntPtr.Zero, "settings XML, checkbox events and batch guards never load native WGC");
        }
        using (var settings = new CrashTWoCLoadRemovalSettings(state, false)) {
            var combo = (ComboBox)Field(settings, "processListComboBox");
            SetField(settings, "refreshingCaptureSources", true);
            combo.Items.Clear(); combo.Items.AddRange(new object[] { "Screen: synthetic", "SyntheticGameExtra: wrong prefix", "SyntheticGame: intended" });
            SetField(settings, "captureIDs", new List<string> { "Screen: synthetic", "SyntheticGameExtra", "SyntheticGame" });
            SetField(settings, "numScreens", 1);
            SetField(settings, "selectedCaptureID", "SyntheticGame"); Call(settings, "UpdateIndexToCaptureID");
            Check(combo.SelectedIndex == 2, "saved capture source uses exact identity instead of a similarly named process prefix");
            SetField(settings, "selectedCaptureID", "MissingSyntheticGame"); Call(settings, "UpdateIndexToCaptureID");
            Check(combo.SelectedIndex == -1, "missing saved source stays unavailable rather than selecting a different source");
            SetField(settings, "selectedCaptureID", ""); Call(settings, "UpdateIndexToCaptureID");
            Check(combo.SelectedIndex == 0, "new settings with no source identity retain the first-monitor default");
            SetField(settings, "refreshingCaptureSources", false);
            var tabs = (TabControl)Field(settings, "tabControl1"); var setup = (TabPage)Field(settings, "tabPage1");
            var timer = (Timer)Field(settings, "capturePreviewRetryTimer");
            tabs.SelectedTab = setup; settings.Visible = true;
            // Only inspect synchronous timer scheduling. There is no message pump,
            // DoEvents, host form or timer tick while capturePreview is enabled.
            SetField(settings, "capturePreview", true);
            try {
                Call(settings, "RetryPendingCapturePreview");
                Check(timer.Enabled && (bool)Field(settings, "capturePreviewRetryRequested"), "Pending preview schedules a retry without capturing synchronously");
                settings.Visible = false;
                Check(!timer.Enabled && (bool)Field(settings, "capturePreviewRetryRequested"), "hiding settings pauses timer while preserving the pending request");
                settings.Visible = true;
                Check(timer.Enabled, "showing settings resumes a pending preview request");
                tabs.SelectedTab = tabs.TabPages[1];
                Check(!timer.Enabled && (bool)Field(settings, "capturePreviewRetryRequested"), "leaving Setup pauses retries while preserving the request");
                tabs.SelectedTab = setup;
                Check(timer.Enabled, "returning to Setup resumes the pending preview request");
                settings.ResetGraphicsCapture();
                Check(!timer.Enabled && !(bool)Field(settings, "capturePreviewRetryRequested"), "capture reset cancels both timer and pending retry request");
                settings.Visible = false; settings.Visible = true;
                Check(!timer.Enabled, "showing settings after reset does not start a new capture request");
            } finally {
                SetField(settings, "capturePreview", false);
                Call(settings, "StopCapturePreviewRetry", true);
            }
            Check(Field(settings, "graphicsCaptureProvider") == null && GetModuleHandle("TwocWindowsGraphicsCapture.dll") == IntPtr.Zero, "retry scheduling lifecycle creates no provider and performs no native capture");
            settings.Dispose();
            Check(Field(settings, "capturePreviewRetryTimer") == null, "disposing settings releases the retry timer");
        }
    }
    static void ComponentPending() {
        var run = new Run(new StandardComparisonGeneratorsFactory()); run.Add(new Segment("Finish"));
        var state = new LiveSplitState(run, null, null, new LiveSplit.Options.LayoutSettings(), new StandardSettingsFactory().Create());
        var type = typeof(CrashTWoCLoadRemovalFactory).Assembly.GetType("LiveSplit.UI.Components.CrashTWoCLoadRemovalComponent", true);
        var component = (IComponent)Activator.CreateInstance(type, new object[] { state, false });
        var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LiveSplit.UI.LayoutMode.Vertical);
        try {
            foreach (string name in new[] { "isLoading", "isBlackScreen", "wasBlackScreen", "isFadeIn", "waitOnFadeIn", "waitOnLoad", "specialLoad", "isCmpFinished" }) SetField(component, name, true);
            SetField(component, "waitFrames", 31); SetField(component, "lowestBit", 17);
            SetField(component, "recognitionKey", "synthetic ready backend"); SetField(component, "recognitionReady", true);
            settings.isCalibratingBlacklevel = true; settings.cmpBlackLevel = 63; state.IsGameTimePaused = true;
            Call(component, "CaptureFailed", new CaptureNotReadyException("Synthetic initial frame"), true);
            bool cleared = true;
            foreach (string name in new[] { "isLoading", "isBlackScreen", "wasBlackScreen", "isFadeIn", "waitOnFadeIn", "waitOnLoad", "specialLoad", "isCmpFinished" }) cleared &= !(bool)Field(component, name);
            Check(cleared && (int)Field(component, "waitFrames") == 0 && (int)Field(component, "lowestBit") == 0 && !state.IsGameTimePaused,
                "component Pending clears stale loading transitions and resumes game time");
            Check(settings.isCalibratingBlacklevel && settings.cmpBlackLevel == 63,
                "component Pending preserves active black-level calibration and its comparison value");
            Check((bool)Field(component, "recognitionReady") && (string)Field(component, "recognitionKey") == "synthetic ready backend"
                && Field(component, "mlClassifier") == null && Field(component, "engine") == null,
                "component Pending preserves backend readiness/key without initializing a recognizer");
            Check(((Label)Field(settings, "captureBackendStatus")).Text.Contains("Synthetic initial frame"), "component Pending reports the retryable capture diagnostic");
        } finally { component.Dispose(); settings.Dispose(); }
    }
    [STAThread] public static int Main() {
        try {
            Lifecycle(); Geometry(); NativeMarshalling(); Settings(); ComponentPending();
            Check(GetModuleHandle("TwocWindowsGraphicsCapture.dll") == IntPtr.Zero, "all default verification finishes without loading native WGC");
            Console.WriteLine("RESULT: " + checks + " GPU-free capture checks passed; " + (IntPtr.Size * 8) + " bit"); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
