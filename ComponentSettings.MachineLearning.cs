using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.CrashTWoCLoadRemover;

namespace LiveSplit.UI.Components
{
    public partial class CrashTWoCLoadRemovalSettings
    {
        public const string ExperimentalMlPlatform = "ENG/PS2 experimental ML";
        private TextBox mlModelPathBox;
        private NumericUpDown mlThresholdBox;
        private Label mlConfidenceLabel;
        private Label mlStatusLabel;
        private TextBox mlSessionBox;
        public event EventHandler MlPreviewRequested;
        public event EventHandler MlReloadRequested;

        public string MlModelPath { get { return mlModelPathBox.Text.Trim(); } }
        public float MlConfidenceThreshold { get { return (float)mlThresholdBox.Value; } }

        private void InitializeMachineLearningSettings()
        {
            var page = new TabPage("Experimental ML") { AutoScroll = true, BackColor = SystemColors.Control };
            tabControl1.TabPages.Add(page);
            page.Controls.Add(new Label { Text = "Vulkan loading-text detector", AutoSize = true, Location = new Point(16, 18) });
            page.Controls.Add(new Label { Text = "Select ENG/PS2 experimental ML on the Setup tab.\nDemo weights use synthetic images. Train on game captures before timing.", Location = new Point(16, 45), Size = new Size(426, 42) });
            page.Controls.Add(new Label { Text = "Model file (relative paths start in Components):", AutoSize = true, Location = new Point(16, 99) });
            mlModelPathBox = new TextBox { Name = "MlModelPath", Text = @"ML\loading-text.twml", Location = new Point(16, 120), Size = new Size(340, 22) };
            page.Controls.Add(mlModelPathBox);
            var browse = new Button { Text = "Browse", Location = new Point(364, 118), Size = new Size(76, 25) };
            browse.Click += (sender, args) =>
            {
                using (var dialog = new OpenFileDialog { Filter = "TWoC ML models (*.twml)|*.twml", CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) mlModelPathBox.Text = dialog.FileName;
            };
            page.Controls.Add(browse);
            page.Controls.Add(new Label { Text = "Loading confidence threshold:", AutoSize = true, Location = new Point(16, 164) });
            mlThresholdBox = new NumericUpDown { Name = "MlConfidenceThreshold", Minimum = 0.001m, Maximum = 1m, DecimalPlaces = 3, Increment = 0.005m, Value = 0.50m, Location = new Point(240, 160), Size = new Size(85, 24) };
            page.Controls.Add(mlThresholdBox);
            mlConfidenceLabel = new Label { Name = "MlConfidence", Text = "Confidence: no prediction yet", AutoSize = true, Location = new Point(16, 201) };
            mlStatusLabel = new Label { Name = "MlStatus", Text = "Select the experimental platform to initialize Vulkan.", Location = new Point(16, 228), Size = new Size(424, 56) };
            page.Controls.Add(mlConfidenceLabel);
            page.Controls.Add(mlStatusLabel);
            var test = new Button { Text = "Test current capture", Location = new Point(16, 291), Size = new Size(168, 29) };
            test.Click += (sender, args) => { if (MlPreviewRequested != null) MlPreviewRequested(this, EventArgs.Empty); };
            var reload = new Button { Text = "Reload model", Location = new Point(196, 291), Size = new Size(130, 29) };
            reload.Click += (sender, args) => { if (MlReloadRequested != null) MlReloadRequested(this, EventArgs.Empty); };
            page.Controls.Add(test);
            page.Controls.Add(reload);
            page.Controls.Add(new Label { Text = "Training samples: use a different session for each recording/run.\nSave both loading text and hard negatives (gameplay, HUD, black).", Location = new Point(16, 341), Size = new Size(425, 35) });
            page.Controls.Add(new Label { Text = "Capture session:", AutoSize = true, Location = new Point(16, 387) });
            mlSessionBox = new TextBox { Text = "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture), Location = new Point(132, 383), Size = new Size(308, 23) };
            page.Controls.Add(mlSessionBox);
            var positive = new Button { Text = "Save LOADING sample", Location = new Point(16, 421), Size = new Size(202, 29) };
            var negative = new Button { Text = "Save non-loading sample", Location = new Point(230, 421), Size = new Size(210, 29) };
            positive.Click += (sender, args) => SaveMachineLearningSample(true);
            negative.Click += (sender, args) => SaveMachineLearningSample(false);
            page.Controls.Add(positive);
            page.Controls.Add(negative);
            page.Controls.Add(new Label { Text = "Samples and samples.csv are saved under Components/ML/samples.\nTrain using the toolchain's ml/README.md instructions.", Location = new Point(16, 466), Size = new Size(425, 36) });
            InitializeTrainingSettings();
        }

        public void UpdateMlStatus(float? confidence, string status, bool error = false)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateMlStatus(confidence, status, error)));
                return;
            }
            mlConfidenceLabel.Text = confidence.HasValue ? "Last confidence: " + confidence.Value.ToString("0.0000", CultureInfo.InvariantCulture) : "Confidence: no prediction yet";
            mlStatusLabel.Text = status;
            mlStatusLabel.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        }

        private void SaveMachineLearningSettings(XmlDocument document, XmlElement settings)
        {
            var path = document.CreateElement("MlModelPath");
            path.InnerText = MlModelPath;
            settings.AppendChild(path);
            var threshold = document.CreateElement("MlConfidenceThreshold");
            threshold.InnerText = MlConfidenceThreshold.ToString("R", CultureInfo.InvariantCulture);
            settings.AppendChild(threshold);
        }

        private void LoadMachineLearningSettings(XmlElement settings)
        {
            if (settings["MlModelPath"] != null) mlModelPathBox.Text = settings["MlModelPath"].InnerText;
            float threshold;
            if (settings["MlConfidenceThreshold"] != null && float.TryParse(settings["MlConfidenceThreshold"].InnerText, NumberStyles.Float, CultureInfo.InvariantCulture, out threshold)
                && !float.IsNaN(threshold) && !float.IsInfinity(threshold) && threshold >= 0.001f && threshold <= 1)
                mlThresholdBox.Value = (decimal)threshold;
        }

        private static string CsvField(string text) { return "\"" + text.Replace("\"", "\"\"") + "\""; }

        private void SaveMachineLearningSample(bool loading)
        {
            try
            {
                string session = mlSessionBox.Text.Trim();
                if (session.Length == 0 || session == "." || session == ".." || session.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new ArgumentException("Enter a valid capture session name.");
                string root = Path.Combine(VulkanLoadingClassifier.ComponentsDirectory, "ML", "samples");
                string label = loading ? "loading" : "not_loading";
                string folder = Path.Combine(root, session, label);
                Directory.CreateDirectory(folder);
                string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture) + ".png";
                using (Bitmap capture = CaptureImage())
                {
                    if (capture.Width < 2 || capture.Height < 2) throw new InvalidOperationException("Capture source is unavailable; sample was not saved.");
                    capture.Save(Path.Combine(folder, name), ImageFormat.Png);
                }
                string manifest = Path.Combine(root, "samples.csv");
                if (!File.Exists(manifest)) File.WriteAllText(manifest, "path,label,group\r\n", new UTF8Encoding(false));
                string relative = session + "/" + label + "/" + name;
                File.AppendAllText(manifest, CsvField(relative) + "," + (loading ? "1" : "0") + "," + CsvField(session) + "\r\n", new UTF8Encoding(false));
                UpdateMlStatus(null, "Saved " + label + " sample for " + session + ".");
            }
            catch (Exception ex) { UpdateMlStatus(null, "Sample not saved: " + ex.Message, true); }
        }
    }
}
