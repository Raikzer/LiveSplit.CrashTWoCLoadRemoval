using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.CrashTWoCLoadRemover;
using LiveSplit.Model;
using LiveSplit.Model.Comparisons;
using LiveSplit.Options.SettingsFactories;
using LiveSplit.UI;
using LiveSplit.UI.Components;

// No game process, screen capture, OCR initialization, native classifier, or GPU.
// Constructors disable capture previews; preview bitmaps are generated in memory.
public static class SettingsControlsVerification
{
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static readonly string[] TransitionFlags = { "isLoading", "isBlackScreen", "wasBlackScreen", "waitOnFadeIn", "waitOnLoad", "specialLoad", "isCmpFinished" };
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
    static T Control<T>(Control parent, string name) where T : Control {
        var found = parent.Controls.Find(name, true);
        if (found.Length != 1 || !(found[0] is T)) throw new Exception("Expected one " + typeof(T).Name + " named " + name);
        return (T)found[0];
    }
    static LiveSplitState State(params string[] segments) {
        var run = new Run(new StandardComparisonGeneratorsFactory()); run.GameName = "Synthetic verification"; run.CategoryName = "Settings";
        foreach (string name in segments.Length == 0 ? new[] { "Finish" } : segments) run.Add(new Segment(name));
        return new LiveSplitState(run, null, null, new LiveSplit.Options.LayoutSettings(), new StandardSettingsFactory().Create());
    }
    static IComponent Component(LiveSplitState state) {
        var type = typeof(CrashTWoCLoadRemovalFactory).Assembly.GetType("LiveSplit.UI.Components.CrashTWoCLoadRemovalComponent", true);
        return (IComponent)Activator.CreateInstance(type, new object[] { state, false });
    }
    static void Load(CrashTWoCLoadRemovalSettings settings, string xml) {
        var document = new XmlDocument(); document.LoadXml(xml); settings.SetSettings(document.DocumentElement);
    }
    static XmlNode Save(CrashTWoCLoadRemovalSettings settings) { return settings.GetSettings(new XmlDocument()); }
    static Bitmap Frame(int width, int height) {
        var bitmap = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(bitmap)) {
            graphics.Clear(Color.DarkSlateBlue); graphics.FillRectangle(Brushes.White, width / 4, height / 3, width / 2, height / 4);
        }
        return bitmap;
    }
    static void SeedTransitions(object component, CrashTWoCLoadRemovalSettings settings, LiveSplitState state) {
        foreach (string name in TransitionFlags) Set(component, name, true);
        Set(component, "waitFrames", 123); Set(component, "lowestBit", 25);
        settings.isCalibratingBlacklevel = true; settings.cmpBlackLevel = 42; state.IsGameTimePaused = true;
    }
    static bool TransitionsClear(object component, CrashTWoCLoadRemovalSettings settings, LiveSplitState state) {
        return TransitionFlags.All(name => !(bool)Get(component, name)) && (int)Get(component, "waitFrames") == 0
            && (int)Get(component, "lowestBit") == 0 && !settings.isCalibratingBlacklevel && settings.cmpBlackLevel == 100 && !state.IsGameTimePaused;
    }

    static void Persistence() {
        using (var settings = new CrashTWoCLoadRemovalSettings(State(), false)) {
            var disable = Control<CheckBox>(settings, "DisableLoadRemover"); var black = Control<NumericUpDown>(settings, "BlacklevelValue");
            Check(disable.Text == "Disable load remover" && !disable.Checked && settings.LoadRemoverEnabled, "load removal defaults enabled with an unchecked Disable load remover checkbox");
            Check(black.Minimum == 0 && black.Maximum == 255 && black.DecimalPlaces == 0 && black.Value == 100 && settings.blacklevel == 100, "black level defaults to 100 with integer range 0 through 255");
            Check(Save(settings)["LoadRemoverEnabled"].InnerText == "True", "default enabled state is saved explicitly");
            disable.Checked = true; black.Value = 87;
            var saved = Save(settings);
            Check(saved["LoadRemoverEnabled"].InnerText == "False" && saved["blacklevel"].InnerText == "87", "disabled state and legacy blacklevel XML key save together");
            using (var restored = new CrashTWoCLoadRemovalSettings(State(), false)) {
                restored.SetSettings(saved);
                Check(!restored.LoadRemoverEnabled && Control<CheckBox>(restored, "DisableLoadRemover").Checked
                    && restored.blacklevel == 87 && Control<NumericUpDown>(restored, "BlacklevelValue").Value == 87, "saved settings restore checkbox inversion and manual black level");
            }
            Load(settings, "<Settings><blacklevel>64</blacklevel></Settings>");
            Check(settings.LoadRemoverEnabled && !disable.Checked && settings.blacklevel == 64 && black.Value == 64, "legacy XML without enabled flag enables load removal and restores blacklevel");
            foreach (string value in new[] { "nonsense", "", "0", "2" }) {
                disable.Checked = true; Load(settings, "<Settings><LoadRemoverEnabled>" + value + "</LoadRemoverEnabled></Settings>");
                Check(settings.LoadRemoverEnabled && !disable.Checked, "invalid enabled XML defaults true: '" + value + "'");
            }
            disable.Checked = true; Load(settings, "<Settings/>");
            Check(settings.LoadRemoverEnabled && settings.blacklevel == 64, "empty legacy settings enable load removal while retaining current blacklevel");
            foreach (string value in new[] { "false", "true" }) {
                Load(settings, "<Settings><LoadRemoverEnabled>" + value + "</LoadRemoverEnabled></Settings>");
                Check(settings.LoadRemoverEnabled == bool.Parse(value), "valid enabled XML is honored: " + value);
            }
            foreach (string value in new[] { "garbage", "", "-1", "256", "999999999999999999999999", "1.5" }) {
                black.Value = 17; Load(settings, "<Settings><blacklevel>" + value + "</blacklevel></Settings>");
                Check(settings.blacklevel == 100 && black.Value == 100, "malformed/out-of-range blacklevel XML safely defaults to 100: '" + value + "'");
            }
            foreach (int value in new[] { 0, 255 }) {
                Load(settings, "<Settings><blacklevel>" + value + "</blacklevel></Settings>");
                Check(settings.blacklevel == value && black.Value == value, "blacklevel XML accepts boundary " + value);
            }
        }
    }

    static void DisableAndGuards() {
        var state = State(); var component = Component(state);
        var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
        try {
            var disable = Control<CheckBox>(settings, "DisableLoadRemover");
            state.CurrentPhase = TimerPhase.Running; state.CurrentSplitIndex = 0; Set(component, "timerStarted", true);
            var loads = (List<int>)Get(component, "NumberOfLoadsPerSplit"); loads[0] = 7;
            settings.AutoSplitterEnabled = true; SeedTransitions(component, settings, state);
            Set(component, "recognitionKey", "stale backend"); Set(component, "recognitionReady", true);
            disable.Checked = true;
            Check(TransitionsClear(component, settings, state), "checking disable immediately resumes game time and clears transition/calibration state before Update");
            Check(Get(component, "mlClassifier") == null && Get(component, "engine") == null && Get(component, "recognitionKey") == null
                && !(bool)Get(component, "recognitionReady"), "disable invalidates recognition key/readiness while backends remain uninitialized");
            Check((bool)Get(component, "timerStarted") && state.CurrentPhase == TimerPhase.Running && state.CurrentSplitIndex == 0 && loads[0] == 7,
                "disable preserves timer phase, split index, started flag, and accumulated loads");
            settings.platform = CrashTWoCLoadRemovalSettings.ExperimentalMlPlatform;
            Set(settings, "processCaptureIndex", int.MaxValue); Set(settings, "processList", new Process[0]);
            // A cached ready key detects accidental recognizer entry, while the
            // invalid process index prevents real capture even on regression.
            {
                string key = settings.platform + "\n" + settings.MlModelPath;
                Set(component, "recognitionKey", key); Set(component, "recognitionReady", true);
                Set(component, "waitFrames", 77); Set(component, "GameName", "stale game metadata"); state.IsGameTimeInitialized = false;
                component.Update(null, state, 0, 0, LayoutMode.Vertical);
                Check((int)Get(component, "waitFrames") == 77 && (string)Get(component, "GameName") == state.Run.GameName
                    && state.CurrentSplitIndex == 0 && loads[0] == 7 && !state.IsGameTimeInitialized && Get(component, "log_file_writer") == null,
                    "disabled Update refreshes passive metadata while skipping capture, recognition, auto-split, log, and game-time initialization");
                Call(component, "OnMlPreviewRequested", null, EventArgs.Empty);
                Check((bool)Get(component, "recognitionReady") && Get(component, "mlClassifier") == null && (int)Get(component, "waitFrames") == 77, "disabled Test current capture bypasses capture/prediction/backend initialization");
                Call(component, "OnMlReloadRequested", null, EventArgs.Empty);
                Check((string)Get(component, "recognitionKey") == key && Get(component, "mlClassifier") == null
                    && (int)Get(component, "waitFrames") == 77, "disabled Reload model bypasses reset and backend initialization");
                Check(!(bool)Call(component, "EnsureRecognitionBackend"), "backend initialization independently rejects disabled state even with cached ready backend");
                Call(settings, "CalibrateBlacklevelButton_Click", null, EventArgs.Empty);
                Call(component, "CalibrateBlacklevel");
                Check(!settings.isCalibratingBlacklevel && (int)Get(component, "waitFrames") == 77 && (bool)Get(component, "recognitionReady"),
                    "disabled settings and component calibration paths do not arm calibration, capture, or reset state");
            }
            Set(component, "recognitionKey", null); Set(component, "recognitionReady", false);
            SeedTransitions(component, settings, state); disable.Checked = false;
            Check(TransitionsClear(component, settings, state) && Get(component, "recognitionKey") == null && !(bool)Get(component, "recognitionReady"), "re-enabling starts with clean recognition transitions and no initialized backend");
            Check((bool)Get(component, "timerStarted") && state.CurrentPhase == TimerPhase.Running && state.CurrentSplitIndex == 0 && loads[0] == 7,
                "re-enabling preserves the running timer and accumulated loads");
            disable.Checked = true; settings.platform = "ENG/PS2";
            using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = settings.Size }) {
                host.Controls.Add(settings); host.Show(); Application.DoEvents();
                Call(settings, "OpenTrainingMode"); var training = (Form)Get(settings, "trainingForm");
                Check(training != null && !training.IsDisposed && Control<Button>(settings, "OpenTrainingMode").Enabled,
                    "explicit training capture/review window remains available while automatic load removal is disabled");
                settings.CloseTrainingMode(); Application.DoEvents();
                Check(training.IsDisposed, "idle explicit training window closes cleanly without capturing a frame");
                host.Controls.Remove(settings); host.Close();
            }
        } finally { component.Dispose(); settings.Dispose(); }
    }

    static void TimerInitialization() {
        foreach (bool pause in new[] { false, true }) {
            var state = State(); var component = Component(state);
            var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
            try {
                var disable = Control<CheckBox>(settings, "DisableLoadRemover"); disable.Checked = true;
                var timer = new TimerModel { CurrentState = state }; timer.Start();
                Check(!state.IsGameTimeInitialized && state.CurrentPhase == TimerPhase.Running && (bool)Get(component, "timerStarted"),
                    "starting while disabled preserves a running timer without initializing game time");
                Check(Get(component, "log_file_writer") == null, "starting while disabled does not open a detection log");
                if (pause) timer.Pause();
                var phase = state.CurrentPhase; bool started = (bool)Get(component, "timerStarted");
                disable.Checked = false;
                Check(state.IsGameTimeInitialized && state.CurrentPhase == phase && (bool)Get(component, "timerStarted") == started,
                    "re-enabling initializes missing game time while preserving " + phase + " phase and started flag");
                state.LoadingTimes = TimeSpan.FromSeconds(9); disable.Checked = true; disable.Checked = false;
                Check(state.IsGameTimeInitialized && state.LoadingTimes == TimeSpan.FromSeconds(9) && state.CurrentPhase == phase,
                    "repeated toggles preserve existing loading times in " + phase + " phase");
            } finally { component.Dispose(); settings.Dispose(); }
        }
        {
            var state = State(); var component = Component(state);
            var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
            try {
                Call(settings, "SetTrainingCaptureActive", true);
                var timer = new TimerModel { CurrentState = state }; timer.Start();
                Check(!state.IsGameTimeInitialized && Get(component, "log_file_writer") == null && (bool)Get(component, "timerStarted"),
                    "starting during explicit training preserves timer bookkeeping without initializing game time or log");
                Call(settings, "SetTrainingCaptureActive", false);
                // A missing model exits before GPU/OCR initialization; Update
                // must initialize game time before its backend readiness check.
                settings.platform = CrashTWoCLoadRemovalSettings.ExperimentalMlPlatform;
                Control<TextBox>(settings, "MlModelPath").Text = "deliberately-missing-settings-verification.twml";
                component.Update(null, state, 0, 0, LayoutMode.Vertical);
                Check(state.IsGameTimeInitialized && state.CurrentPhase == TimerPhase.Running && (bool)Get(component, "timerStarted"),
                    "first enabled Update initializes missing game time for an active timer after training");
            } finally { component.Dispose(); settings.Dispose(); }
        }
    }

    static void DisabledSplitBookkeeping() {
        var state = State("A", "B", "C"); state.Run.Offset = TimeSpan.FromSeconds(1);
        var component = Component(state);
        var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
        try {
            Control<CheckBox>(settings, "DisableLoadRemover").Checked = true;
            var timer = new TimerModel { CurrentState = state }; timer.Start();
            state.Run[0].Name = "A2";
            component.Update(null, state, 0, 0, LayoutMode.Vertical);
            Check(settings.GetCumulativeNumberOfLoadsForSplit("A2") == 2 && settings.GetCumulativeNumberOfLoadsForSplit("C") == 6,
                "disabled Update refreshes renamed split requirements before manual timer events");
            timer.Split(); var loads = (List<int>)Get(component, "NumberOfLoadsPerSplit");
            Check(state.CurrentSplitIndex == 1 && loads[0] == 2 && loads[1] == 0,
                "manual split after disabled rename records the first split's two loads rather than the stale six-load total");
            Check(!state.IsGameTimeInitialized && Get(component, "log_file_writer") == null && Get(component, "mlClassifier") == null && Get(component, "engine") == null,
                "passive split refresh and manual split do not initialize game time, logs, or recognition while disabled");
        } finally { component.Dispose(); settings.Dispose(); }
    }

    static void DisabledRunCountBookkeeping() {
        var state = State("A", "B", "C"); var component = Component(state);
        var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
        try {
            Control<CheckBox>(settings, "DisableLoadRemover").Checked = true;
            var timer = new TimerModel { CurrentState = state }; timer.Start();
            var loads = (List<int>)Get(component, "NumberOfLoadsPerSplit"); loads[0] = 3; loads[1] = 4; loads[2] = 5;
            state.Run.Add(new Segment("D"));
            component.Update(null, state, 0, 0, LayoutMode.Vertical);
            loads = (List<int>)Get(component, "NumberOfLoadsPerSplit");
            Check(loads.SequenceEqual(new[] { 3, 4, 5, 0, 99999 }) && (int)Get(component, "NumberOfSplits") == 4
                && ((List<string>)Get(component, "SplitNames")).SequenceEqual(new[] { "A", "B", "C", "D" }),
                "adding a split to the same disabled Run preserves real load counts and moves the terminal sentinel past a zeroed new entry");
            state.Run.RemoveAt(3); state.Run.RemoveAt(2);
            component.Update(null, state, 0, 0, LayoutMode.Vertical);
            loads = (List<int>)Get(component, "NumberOfLoadsPerSplit");
            Check(loads.SequenceEqual(new[] { 3, 4, 99999 }) && (int)Get(component, "NumberOfSplits") == 2
                && ((List<string>)Get(component, "SplitNames")).SequenceEqual(new[] { "A", "B" }),
                "removing splits from the same disabled Run truncates removed counts while preserving real entries and a single terminal sentinel");
            state.Run.Add(new Segment("E"));
            component.Update(null, state, 0, 0, LayoutMode.Vertical);
            loads = (List<int>)Get(component, "NumberOfLoadsPerSplit");
            Check(loads.SequenceEqual(new[] { 3, 4, 0, 99999 }) && (int)Get(component, "NumberOfSplits") == 3,
                "growing a previously shortened Run initializes its new real count to zero rather than reusing the sentinel or a removed count");
            Check(state.CurrentSplitIndex == 0 && !state.IsGameTimeInitialized && Get(component, "log_file_writer") == null
                && Get(component, "mlClassifier") == null && Get(component, "engine") == null,
                "same-Run count refresh remains passive while disabled");
        } finally { component.Dispose(); settings.Dispose(); }
    }

    static Rectangle Region(CrashTWoCLoadRemovalSettings settings) { return (Rectangle)Get(settings, "selectionRectanglePreviewBox"); }
    static void CheckRegion(CrashTWoCLoadRemovalSettings settings, Rectangle expected, string message) {
        var actual = Region(settings);
        Check(actual == expected && (Point)Get(settings, "selectionTopLeft") == expected.Location
            && (Point)Get(settings, "selectionBottomRight") == new Point(expected.Right, expected.Bottom)
            && Control<NumericUpDown>(settings, "CaptureLeft").Value == expected.Left
            && Control<NumericUpDown>(settings, "CaptureTop").Value == expected.Top
            && Control<NumericUpDown>(settings, "CaptureRight").Value == expected.Right
            && Control<NumericUpDown>(settings, "CaptureBottom").Value == expected.Bottom, message);
    }
    static string RegionXml(string x, string y, string width, string height) {
        return "<Settings><CaptureRegion><X>" + x + "</X><Y>" + y + "</Y><Width>" + width + "</Width><Height>" + height + "</Height></CaptureRegion></Settings>";
    }
    static void CropCoordinates() {
        var state = State(); var component = Component(state);
        var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
        try {
            var left = Control<NumericUpDown>(settings, "CaptureLeft"); var top = Control<NumericUpDown>(settings, "CaptureTop");
            var right = Control<NumericUpDown>(settings, "CaptureRight"); var bottom = Control<NumericUpDown>(settings, "CaptureBottom");
            Check(new[] { left, top, right, bottom }.All(control => control.DecimalPlaces == 0 && control.Increment == 1),
                "all four capture boundaries use integer preview coordinates and one-unit arrow steps");
            CheckRegion(settings, new Rectangle(0, 0, 400, 160), "default crop is the entire 400 by 160 preview and all four controls/points agree");
            SeedTransitions(component, settings, state);
            left.Value = 20; top.Value = 10; right.Value = 320; bottom.Value = 130;
            var numeric = new Rectangle(20, 10, 300, 120);
            CheckRegion(settings, numeric, "four numeric edits update the authoritative rectangle and both selection points");
            Check(TransitionsClear(component, settings, state), "numeric crop edits clear recognition transitions and cancel stale calibration");
            var saved = Save(settings); var crop = saved["CaptureRegion"];
            Check(crop["X"].InnerText == "20" && crop["Y"].InnerText == "10" && crop["Width"].InnerText == "300" && crop["Height"].InnerText == "120",
                "numeric capture boundaries persist through existing CaptureRegion X/Y/Width/Height XML");
            using (var restored = new CrashTWoCLoadRemovalSettings(State(), false)) {
                restored.SetSettings(saved); CheckRegion(restored, numeric, "saved numeric capture region round-trips exactly without screen capture");
            }
            Load(settings, RegionXml("17", "23", "286", "109"));
            CheckRegion(settings, new Rectangle(17, 23, 286, 109), "valid legacy capture rectangle loads exactly and synchronizes all four controls");
            Call(settings, "SetRectangleFromMouse", new MouseEventArgs(MouseButtons.Left, 1, 30, 40, 0));
            CheckRegion(settings, new Rectangle(30, 40, 273, 92), "left-click coordinates synchronize top-left controls and preserve bottom-right");
            Call(settings, "SetRectangleFromMouse", new MouseEventArgs(MouseButtons.Right, 1, 350, 145, 0));
            CheckRegion(settings, new Rectangle(30, 40, 320, 105), "right-click coordinates synchronize bottom-right controls and preserve top-left");
            Call(settings, "SetRectangleFromMouse", new MouseEventArgs(MouseButtons.Left, 1, -500, -500, 0));
            Call(settings, "SetRectangleFromMouse", new MouseEventArgs(MouseButtons.Right, 1, 1000, 1000, 0));
            CheckRegion(settings, new Rectangle(0, 0, 400, 160), "out-of-preview mouse coordinates clamp to a full valid rectangle");
            Load(settings, RegionXml("399", "159", "1", "1"));
            var singlePixel = new Rectangle(399, 159, 1, 1);
            CheckRegion(settings, singlePixel, "one-pixel crop at the final preview coordinate is accepted");
            left.Value = Math.Min(left.Maximum, 400); right.Value = Math.Max(right.Minimum, 399);
            top.Value = Math.Min(top.Maximum, 160); bottom.Value = Math.Max(bottom.Minimum, 159);
            CheckRegion(settings, singlePixel, "crossed or equal numeric edges preserve the last valid nonempty rectangle");
            Call(settings, "SetRectangleFromMouse", new MouseEventArgs(MouseButtons.Left, 1, 400, 160, 0));
            Call(settings, "SetRectangleFromMouse", new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0));
            CheckRegion(settings, singlePixel, "crossed mouse edges cannot create an empty or negative crop");
            foreach (string xml in new[] { RegionXml("broken", "0", "10", "10"), "<Settings><CaptureRegion><X>1</X></CaptureRegion></Settings>", RegionXml("99999999999999999999", "0", "10", "10") }) {
                Load(settings, xml); CheckRegion(settings, new Rectangle(0, 0, 400, 160), "malformed or incomplete capture XML safely resets all coordinates to the full preview");
            }
            foreach (string xml in new[] { RegionXml("-20", "-30", "500", "300"), RegionXml("600", "300", "30", "20"), RegionXml("20", "10", "-5", "0"), RegionXml("2147483647", "2147483647", "2147483647", "2147483647") }) {
                Load(settings, xml); var actual = Region(settings);
                Check(actual.Left >= 0 && actual.Top >= 0 && actual.Right <= 400 && actual.Bottom <= 160 && actual.Width >= 1 && actual.Height >= 1,
                    "parsed out-of-range capture XML always produces a bounded nonempty region: " + xml);
                CheckRegion(settings, actual, "normalized XML rectangle, points, and all numeric controls remain synchronized");
            }
        } finally { component.Dispose(); settings.Dispose(); }
    }

    static void BlacklevelAndLayout() {
        var state = State(); var component = Component(state);
        var settings = (CrashTWoCLoadRemovalSettings)component.GetSettingsControl(LayoutMode.Vertical);
        try {
            var black = Control<NumericUpDown>(settings, "BlacklevelValue"); int changes = 0;
            settings.DetectionConfigurationChanged += (sender, args) => changes++;
            SeedTransitions(component, settings, state); black.Value = 63;
            Check(settings.blacklevel == 63 && TransitionsClear(component, settings, state) && changes == 1,
                "manual blacklevel edit cancels calibration and immediately clears pending recognition state once");
            foreach (int value in new[] { 0, 255 }) { black.Value = value; Check(settings.blacklevel == value, "manual blacklevel accepts boundary " + value); }
            int priorChanges = changes;
            settings.isCalibratingBlacklevel = true; settings.cmpBlackLevel = 51; settings.blacklevel = 52;
            Set(component, "waitFrames", 81); state.IsGameTimePaused = true; settings.UpdateBlacklevelLabel();
            Check(black.Value == 52 && settings.isCalibratingBlacklevel && settings.cmpBlackLevel == 51 && changes == priorChanges
                && (int)Get(component, "waitFrames") == 81 && state.IsGameTimePaused,
                "calibration-driven blacklevel UI synchronization preserves calibration and pending transition state");
            settings.blacklevel = -5; settings.UpdateBlacklevelLabel(); Check(black.Value == 0 && settings.isCalibratingBlacklevel && changes == priorChanges, "UI synchronization clamps low display values without firing manual edit effects");
            settings.blacklevel = 999; settings.UpdateBlacklevelLabel(); Check(black.Value == 255 && settings.isCalibratingBlacklevel && changes == priorChanges, "UI synchronization clamps high display values without firing manual edit effects");
            settings.blacklevel = 71; settings.isCalibratingBlacklevel = false; settings.UpdateBlacklevelLabel();
            using (var host = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = settings.Size }) {
                host.Controls.Add(settings); host.Show(); Application.DoEvents();
                var tabs = (TabControl)Get(settings, "tabControl1"); tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Setup");
                var preview = (PictureBox)Get(settings, "previewPictureBox"); var cropped = (PictureBox)Get(settings, "croppedPreviewPictureBox");
                using (var full = Frame(preview.Width, preview.Height)) using (var crop = Frame(cropped.Width, cropped.Height)) {
                    preview.Image = full; cropped.Image = crop;
                    Application.DoEvents();
                    using (var image = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(image, settings.ClientRectangle); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-setup.png")); }
                    var setup = tabs.SelectedTab; setup.AutoScrollPosition = new Point(0, setup.AutoScrollMinSize.Height); Application.DoEvents();
                    using (var image = new Bitmap(settings.Width, settings.Height)) { settings.DrawToBitmap(image, settings.ClientRectangle); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings-setup-scrolled.png")); }
                    preview.Image = null; cropped.Image = null;
                }
                Check(black.Visible && black.Enabled && black.Width >= 50 && tabs.SelectedTab.AutoScroll, "Setup exposes an editable blacklevel and scrollable controls in the offscreen host");
                host.Controls.Remove(settings); host.Close();
            }
        } finally { component.Dispose(); settings.Dispose(); }
    }

    [STAThread] public static int Main() {
        try {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            ComponentManager.RaceProviderFactories = new Dictionary<string, IRaceProviderFactory>();
            Persistence(); DisableAndGuards(); TimerInitialization(); DisabledSplitBookkeeping(); DisabledRunCountBookkeeping(); CropCoordinates(); BlacklevelAndLayout();
            Console.WriteLine("RESULT: " + checks + " settings checks passed; synthetic/offscreen only; " + (IntPtr.Size * 8) + " bit"); return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
