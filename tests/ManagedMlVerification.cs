using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.CrashTWoCLoadRemover;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options;
using LiveSplit.Options.SettingsFactories;
using LiveSplit.UI;
using LiveSplit.UI.Components;
using Tesseract;

// Deterministic integration test: no window automation, screen pixels, or game process.
// Uses the production constructors with capturePreview=false. Runtime capture stays on
// its deterministic 1x1 invalid-source path; transition fixtures inject synthetic bitmaps.
public static class ManagedMlVerification
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static int checks;
    static string Root { get { return AppDomain.CurrentDomain.BaseDirectory; } }
    static string Model { get { return Path.Combine(VulkanLoadingClassifier.ComponentsDirectory, "ML", "loading-text.twml"); } }
    static string Shaders { get { return Path.Combine(VulkanLoadingClassifier.ComponentsDirectory, "ML", "shaders"); } }
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
    static FieldInfo Field(object obj, string name) { return obj.GetType().GetField(name, Flags); }
    static object Get(object obj, string name) { return Field(obj, name).GetValue(obj); }
    static void Set(object obj, string name, object value) { Field(obj, name).SetValue(obj, value); }
    static object Call(object obj, string method, params object[] args) {
        try { return obj.GetType().GetMethod(method, Flags).Invoke(obj, args); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    static bool Loaded(string name) { return GetModuleHandle(name) != IntPtr.Zero; }
    static XmlElement Xml(string body) { var d = new XmlDocument(); d.LoadXml("<Settings>" + body + "</Settings>"); return d.DocumentElement; }
    static string Status(CrashTWoCLoadRemovalSettings s) { return ((Label)s.Controls.Find("MlStatus", true)[0]).Text; }
    static Bitmap Solid(Color color, int size = 100) { var b = new Bitmap(size, size, PixelFormat.Format32bppArgb); using (var g = Graphics.FromImage(b)) g.Clear(color); return b; }
    static LiveSplitState State() {
        var run = new Run(new StandardComparisonGeneratorsFactory()); run.GameName = "Crash Bandicoot: The Wrath of Cortex"; run.CategoryName = "Practice"; run.Add(new Segment("Finish"));
        return new LiveSplitState(run, null, null, new LiveSplit.Options.LayoutSettings(), new StandardSettingsFactory().Create());
    }
    static IComponent Component(LiveSplitState state) {
        Type t = typeof(CrashTWoCLoadRemovalFactory).Assembly.GetType("LiveSplit.UI.Components.CrashTWoCLoadRemovalComponent", true);
        var c = (IComponent)Activator.CreateInstance(t, new object[] { state, false });
        var s = (CrashTWoCLoadRemovalSettings)c.GetSettingsControl(LayoutMode.Vertical);
        Set(s, "processCaptureIndex", int.MaxValue); Set(s, "processList", new Process[0]);
        return c;
    }
    static void WaitForNativeRelease() {
        var wait = Stopwatch.StartNew();
        while (Loaded("TwocLoadingVulkan.dll") && wait.ElapsedMilliseconds < 10000) Thread.Sleep(20);
        if (Loaded("TwocLoadingVulkan.dll")) throw new Exception("Native cleanup did not finish within 10 seconds");
    }
    static void RenderSettings(CrashTWoCLoadRemovalSettings settings) {
        // Give native WinForms edit controls real handles before DrawToBitmap.
        // The offscreen fixture window is not a desktop screenshot or UI automation.
        using (var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = settings.Size }) {
            form.Controls.Add(settings); form.Show(); Application.DoEvents();
            var tabs = (TabControl)Get(settings, "tabControl1");
            int mlTab = tabs.TabPages.Cast<TabPage>().ToList().FindIndex(page => page.Text == "Experimental ML");
            foreach (int tab in new[] { 0, mlTab }) {
                tabs.SelectedIndex = tab; Application.DoEvents(); settings.Refresh();
                using (var rendered = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(rendered, settings.ClientRectangle); rendered.Save(Path.Combine(Root, tab == 0 ? "settings-setup.png" : "settings-ml.png"), System.Drawing.Imaging.ImageFormat.Png); }
            }
            form.Controls.Remove(settings);
        }
    }
    static void Select(CrashTWoCLoadRemovalSettings s, string platform, string model = @"ML\loading-text.twml") {
        s.SetSettings(Xml("<platform>" + System.Security.SecurityElement.Escape(platform) + "</platform><MlModelPath>" + System.Security.SecurityElement.Escape(model) + "</MlModelPath><MlConfidenceThreshold>0.52</MlConfidenceThreshold><SaveDetectionLog>False</SaveDetectionLog>"));
    }
    static bool Ensure(object c) { return (bool)Call(c, "EnsureRecognitionBackend"); }
    static void Reload(object c) { Call(c, "OnMlReloadRequested", null, EventArgs.Empty); }
    static void ConstantModel(string path, float bias) {
        byte[] bytes = File.ReadAllBytes(Model); Array.Clear(bytes, 32, bytes.Length - 32); Array.Copy(BitConverter.GetBytes(bias), 0, bytes, bytes.Length - 4, 4); File.WriteAllBytes(path, bytes);
    }
    static float[] Npy(string file) {
        using (var r = new BinaryReader(File.OpenRead(file))) { r.ReadBytes(6); byte major = r.ReadByte(); r.ReadByte(); int header = major == 1 ? r.ReadUInt16() : (int)r.ReadUInt32(); r.ReadBytes(header);
            var result = new float[96 * 96]; for (int i = 0; i < result.Length; i++) result[i] = r.ReadSingle(); Check(r.BaseStream.Position == r.BaseStream.Length, "parity fixture has exactly 9216 float32 values"); return result; }
    }
    static void Parity() {
        var expected = new JavaScriptSerializer().Deserialize<Dictionary<string, object>[]>(File.ReadAllText(Path.Combine(Root, "parity-samples", "expected.json")));
        using (var classifier = new VulkanLoadingClassifier(Model, Shaders)) {
            Console.WriteLine("VULKAN DEVICE: " + classifier.DeviceName); Check(!string.IsNullOrWhiteSpace(classifier.DeviceName), "managed wrapper reports an actual Vulkan device");
            foreach (var sample in expected) {
                string id = sample["sample"].ToString(); float[] target = Npy(Path.Combine(Root, "parity-samples", id + ".npy"));
                using (var b = new Bitmap(Path.Combine(Root, "parity-samples", id + ".png"))) {
                    float[] actual = VulkanLoadingClassifier.ImageToTensor(b); double max = actual.Zip(target, (a, t) => Math.Abs((double)a - t)).Max();
                    Check(max <= 1e-7, "RGB bilinear preprocessing parity sample " + id + "; max error=" + max.ToString("R", CultureInfo.InvariantCulture));
                    double prediction = classifier.Predict(b), reference = Convert.ToDouble(sample["confidence"], CultureInfo.InvariantCulture);
                    Console.WriteLine("PREDICTION " + id + ": GPU=" + prediction.ToString("R", CultureInfo.InvariantCulture) + " CPU=" + reference.ToString("R", CultureInfo.InvariantCulture));
                    Check(prediction >= 0 && prediction <= 1 && Math.Abs(prediction - reference) <= 1e-4, "Vulkan confidence matches CPU fixture " + id);
                }
            }
            var bad = new float[9216]; bad[4] = float.NaN;
            try { classifier.PredictTensor(bad); Check(false, "NaN tensor rejected"); } catch (InvalidOperationException ex) { Check(ex.Message.Contains("finite"), "native inference error crosses managed boundary visibly"); }
        }
        var disposed = new VulkanLoadingClassifier(Model, Shaders); disposed.Dispose(); disposed.Dispose();
        try { disposed.PredictTensor(new float[9216]); Check(false, "disposed wrapper rejects inference"); } catch (ObjectDisposedException) { Check(true, "wrapper disposal is idempotent and invalidates context"); }
    }
    static void Failure(IComponent c, CrashTWoCLoadRemovalSettings s, LiveSplitState state, string label, Action breakAsset, Action repairAsset) {
        Call(c, "DisposeRecognitionBackend"); WaitForNativeRelease(); Set(c, "recognitionKey", null); state.IsGameTimePaused = true;
        breakAsset();
        try {
            Check(!Ensure(c), label + " fails safely"); string status = Status(s); Console.WriteLine("EXPECTED FAILURE " + label + ": " + status);
            Check(status.Contains("unavailable") && ((Label)s.Controls.Find("MlStatus", true)[0]).ForeColor == Color.Firebrick, label + " is visible as an error");
            Check(Get(c, "engine") == null && Get(c, "mlClassifier") == null && !state.IsGameTimePaused, label + " releases game time and has no CPU/OCR fallback");
        } finally { WaitForNativeRelease(); repairAsset(); }
        Check(!Ensure(c), label + " stays failed after asset repair until explicit reload");
        Reload(c); Check((bool)Get(c, "recognitionReady"), label + " recovers after explicit reload");
    }
    static int BlockingNative() {
        string marker = Path.Combine(Root, "destroy-completed.txt"); if (File.Exists(marker)) File.Delete(marker);
        Environment.SetEnvironmentVariable("TWOC_TEST_DESTROY_MARKER", marker);
        var state = State(); var component = Component(state); var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
        Select(settings, "ENG/PS2 experimental ML"); Check(Ensure(component), "blocking cleanup fixture creates native context");
        state.IsGameTimePaused = true;
        var watch = Stopwatch.StartNew();
        // Inject a valid capture at the recognition seam; use the actual failure handler
        // used by CaptureLoads/preview. Their catch boundary is tested separately with 1x1.
        using (var image = Solid(Color.White)) {
            try { Call(component, "RecognizeLoadingText", image, false); Check(false, "fault fixture must fail inference"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("injected native inference failure"), "actual native inference failure crosses wrapper"); Call(component, "RecognitionFailed", ex); }
        }
        watch.Stop();
        Console.WriteLine("BLOCKING CLEANUP RETURN: " + watch.ElapsedMilliseconds + "ms");
        Check(watch.ElapsedMilliseconds < 1000, "recognition failure returns within 1s while destroy blocks 2s");
        Check(!state.IsGameTimePaused && Status(settings).Contains("injected native inference failure"), "game time is released and native error is immediately visible");
        Check(Get(component, "mlClassifier") == null && Get(component, "engine") == null, "failed context is detached with no OCR fallback");
        Check(Loaded("TwocLoadingVulkan.dll") && !File.Exists(marker), "DLL remains loaded while native destructor is still running");
        var wait = Stopwatch.StartNew(); while (!File.Exists(marker) && wait.ElapsedMilliseconds < 5000) Thread.Sleep(20);
        Check(File.Exists(marker), "blocked native destructor eventually completes");
        WaitForNativeRelease(); Check(!Loaded("TwocLoadingVulkan.dll"), "DLL unloads after native cleanup completes");
        Check(!Ensure(component), "failed backend does not automatically reinitialize");
        component.Dispose(); settings.Dispose(); Console.WriteLine("RESULT: " + checks + " checks passed"); return 0;
    }
    [STAThread] public static int Main(string[] args) {
        try {
            Console.WriteLine("Runtime " + Environment.Version + "; architecture=" + (IntPtr.Size * 8));
            ComponentManager.RaceProviderFactories = new Dictionary<string, IRaceProviderFactory>();
            if (args.Length != 0 && args[0] == "--blocking-native") return BlockingNative();
            if (args.Length != 0 && args[0] == "--render-settings") {
                var renderComponent = Component(State()); var renderSettings = (CrashTWoCLoadRemovalSettings)renderComponent.GetSettingsControl(LayoutMode.Vertical);
                Select(renderSettings, "ENG/PS2 experimental ML"); RenderSettings(renderSettings); renderComponent.Dispose(); renderSettings.Dispose(); return 0;
            }
            Check(!Loaded("TwocLoadingVulkan.dll") && !Loaded("vulkan-1.dll"), "ML native runtime and Vulkan are lazy before use");
            var state = State(); var component = Component(state); var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
            Check(CrashTWoCLoadRemovalSettings.ExperimentalMlPlatform == "ENG/PS2 experimental ML", "exact experimental platform label");
            var platforms = (ComboBox)Get(settings, "platformComboBox");
            Check(new[] { "ENG/PS2", "JPN/PS2", "KRN/PS2", "ENG/XBOX&GC", "ENG/PS2 experimental ML" }.All(p => platforms.Items.Contains(p)), "legacy platforms retained and experimental choice present");
            Select(settings, "ENG/PS2"); bool legacyReady = Ensure(component); Console.WriteLine("LEGACY STATUS: " + Status(settings)); Check(legacyReady, "legacy English OCR initializes");
            var english = (TesseractEngine)Get(component, "engine"); Check(!Loaded("TwocLoadingVulkan.dll") && !Loaded("vulkan-1.dll"), "legacy OCR does not load Vulkan");
            Select(settings, "ENG/PS2 experimental ML", "missing.twml"); Check(!Ensure(component), "missing model rejects ML initialization");
            Check(!Loaded("TwocLoadingVulkan.dll") && !Loaded("vulkan-1.dll") && english.IsDisposed, "missing model does not load Vulkan and switching disposes OCR");
            CultureInfo previous = CultureInfo.CurrentCulture; CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Select(settings, "ENG/PS2 experimental ML"); var doc = new XmlDocument(); var saved = (XmlElement)settings.GetSettings(doc);
            Check(saved["MlConfidenceThreshold"].InnerText == "0.52" && saved["MlModelPath"].InnerText == @"ML\loading-text.twml", "settings XML uses invariant threshold and preserves model path");
            settings.SetSettings(saved); Check(Math.Abs(settings.MlConfidenceThreshold - .52f) < .00001f && settings.MlModelPath == @"ML\loading-text.twml", "model path and confidence threshold round-trip under de-DE");
            CultureInfo.CurrentCulture = previous;
            RenderSettings(settings);
            var capture = (CrashTWoCLoadDetector.ImageCaptureInfo)Get(settings, "imageCaptureInfo");
            Check(capture.resizeState == CrashTWoCLoadDetector.ResizeState.ENG && capture.resizeSizeXEng == 100 && capture.resizeSizeYEng == 100, "ML selection uses ENG capture resize (100x100)");
            Check(Ensure(component) && Get(component, "engine") == null, "ML initializes without an OCR engine");
            var beforeDisable = (VulkanLoadingClassifier)Get(component, "mlClassifier");
            var disableLoadRemover = (CheckBox)settings.Controls.Find("DisableLoadRemover", true).Single();
            state.IsGameTimePaused = true; Set(component, "waitOnLoad", true); Set(component, "waitFrames", 42);
            disableLoadRemover.Checked = true;
            Check(!state.IsGameTimePaused && Get(component, "mlClassifier") == null && Get(component, "engine") == null
                && Get(component, "recognitionKey") == null && !(bool)Get(component, "recognitionReady")
                && !(bool)Get(component, "waitOnLoad") && (int)Get(component, "waitFrames") == 0,
                "disabling an initialized ML backend immediately releases game time and clears backend/recognition state");
            try { beforeDisable.PredictTensor(new float[9216]); Check(false, "disabled ML disposed"); }
            catch (ObjectDisposedException) { Check(true, "disable disposes the initialized ML context"); }
            disableLoadRemover.Checked = false;
            Check(Get(component, "mlClassifier") == null && Ensure(component) && !ReferenceEquals(beforeDisable, Get(component, "mlClassifier")),
                "re-enabling initializes a fresh ML backend on demand");
            var first = (VulkanLoadingClassifier)Get(component, "mlClassifier");
            Select(settings, "ENG/PS2"); Check(Ensure(component), "ML can switch back to legacy OCR");
            try { first.PredictTensor(new float[9216]); Check(false, "old ML disposed"); } catch (ObjectDisposedException) { Check(true, "switch to legacy disposes ML context"); }
            english = (TesseractEngine)Get(component, "engine"); Select(settings, "ENG/PS2 experimental ML"); Check(Ensure(component) && english.IsDisposed, "switch back to ML disposes OCR and creates fresh context");
            Parity();
            string native = Path.Combine(VulkanLoadingClassifier.ComponentsDirectory, IntPtr.Size == 8 ? "x64" : "x86", "TwocLoadingVulkan.dll");
            Select(settings, "ENG/PS2 experimental ML");
            Failure(component, settings, state, "missing model", () => File.Move(Model, Model + ".bak"), () => File.Move(Model + ".bak", Model));
            byte[] model = File.ReadAllBytes(Model);
            Failure(component, settings, state, "corrupt model", () => File.WriteAllBytes(Model, new byte[32]), () => File.WriteAllBytes(Model, model));
            Failure(component, settings, state, "missing native", () => File.Move(native, native + ".bak"), () => File.Move(native + ".bak", native));
            byte[] library = File.ReadAllBytes(native);
            Failure(component, settings, state, "corrupt native", () => File.WriteAllBytes(native, new byte[32]), () => File.WriteAllBytes(native, library));
            Failure(component, settings, state, "missing shaders", () => Directory.Move(Shaders, Shaders + ".bak"), () => Directory.Move(Shaders + ".bak", Shaders));
            string shader = Path.Combine(Shaders, "conv1.spv"); byte[] shaderBytes = File.ReadAllBytes(shader);
            Failure(component, settings, state, "corrupt shader", () => File.WriteAllBytes(shader, new byte[4]), () => File.WriteAllBytes(shader, shaderBytes));
            string positive = Path.Combine(Root, "positive.twml"), negative = Path.Combine(Root, "negative.twml"); ConstantModel(positive, 10); ConstantModel(negative, -10);
            Select(settings, "ENG/PS2 experimental ML", positive); Check(Ensure(component), "constant-positive synthetic model initializes");
            var timer = new TimerModel { CurrentState = state }; timer.Start();
            using (var black = Solid(Color.Black)) using (var white = Solid(Color.White)) {
                Check(!(bool)Call(component, "ProcessPs2Capture", black) && state.IsGameTimePaused, "PS2 black pre-transition pauses game time");
                Call(component, "ProcessPs2Capture", white); Check((bool)Get(component, "waitOnLoad") && !state.IsGameTimePaused, "black-to-image transition arms loading-text detection");
                Check((bool)Call(component, "ProcessPs2Capture", white) && state.IsGameTimePaused && (bool)Get(component, "waitOnFadeIn"), "positive GPU prediction confirms PS2 load and pauses game time");
                Check(Get(component, "engine") == null && (bool)Call(component, "RecognizeLoadingText", white, true), "ML calibration recognizes raw capture without Korean/OCR engine");
                Call(component, "ProcessPs2Capture", black); Call(component, "ProcessPs2Capture", white);
                Check(!state.IsGameTimePaused && !(bool)Get(component, "isLoading"), "PS2 post-load black transition resumes game time");
                Select(settings, "ENG/PS2 experimental ML", negative); Check(Ensure(component) && !state.IsGameTimePaused, "changing model resets transitions and resumes game time");
                Call(component, "ProcessPs2Capture", black); Call(component, "ProcessPs2Capture", white);
                Check(!(bool)Call(component, "ProcessPs2Capture", white) && !state.IsGameTimePaused, "negative GPU prediction keeps game time running");
            }
            // Exercise the production Update/CaptureLoads catch boundary with an unavailable source.
            // Full XML round trips can restore the original display selection. Override it here.
            Set(settings, "processCaptureIndex", int.MaxValue); Set(settings, "processList", new Process[0]);
            state.IsGameTimePaused = true; Set(component, "timerStarted", true); Set(component, "GameName", state.Run.GameName); Set(component, "GameCategory", state.Run.CategoryName);
            Set(component, "NumberOfSplits", state.Run.Count); Set(component, "SplitNames", state.Run.Select(x => x.Name).ToList());
            var captureBackend = Get(component, "mlClassifier");
            component.Update(null, state, 0, 0, LayoutMode.Vertical);
            Console.WriteLine("INVALID CAPTURE STATUS: " + Status(settings));
            Check(!state.IsGameTimePaused && (bool)Get(component, "recognitionReady") && Status(settings).Contains("Capture unavailable"), "invalid capture releases game time and shows a capture-specific error while preserving recognition");
            Check(captureBackend != null && Ensure(component) && ReferenceEquals(captureBackend, Get(component, "mlClassifier")), "capture failure keeps the valid model available for the next frame"); Reload(component); Check((bool)Get(component, "recognitionReady"), "explicit reload still rebuilds recognition after a capture failure");
            state.IsGameTimePaused = true; Call(component, "RecognitionFailed", new InvalidOperationException("injected post-call GPU failure"));
            Check(!state.IsGameTimePaused && Get(component, "mlClassifier") == null && Status(settings).Contains("injected post-call GPU failure"), "post-call failure disposes context, releases game time and appears in status");
            component.Dispose(); component.Dispose(); settings.Dispose();
            Console.WriteLine("RESULT: " + checks + " checks passed"); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
