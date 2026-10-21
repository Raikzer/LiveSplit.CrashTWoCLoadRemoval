using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Xml;
using LiveSplit.CrashTWoCLoadRemover;
using LiveSplit.CrashTWoCLoadRemover.Training;

namespace LiveSplit.UI.Components
{
    public partial class CrashTWoCLoadRemovalSettings
    {
        private TrainingCaptureForm trainingForm;
        private TabPage trainingPage;
        private Label trainingModeStatus;
        private readonly Dictionary<Control, bool> trainingDisabledControls = new Dictionary<Control, bool>();
        private Form trainingOwner;
        private bool closingTrainingSettings;
        public bool TrainingCaptureActive { get; private set; }
        public event EventHandler TrainingCaptureStateChanged;

        private void InitializeTrainingSettings()
        {
            trainingPage = new TabPage("Training") { AutoScroll = true, BackColor = SystemColors.Control };
            tabControl1.TabPages.Add(trainingPage);
            trainingPage.Controls.Add(new Label { Text = "Capture and review training frames", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Location = new Point(16, 20) });
            trainingPage.Controls.Add(new Label
            {
                Text = "Configure the capture source and crop on Setup first.\nUse ENG/PS2 or ENG/PS2 experimental ML. No model is needed to collect frames.",
                Location = new Point(16, 52), Size = new Size(422, 50)
            });
            trainingPage.Controls.Add(new Label
            {
                Text = "Default: 20,000 cropped images at 30 FPS (about 11 minutes).\nThe capture window stays open while you play. You can stop early and review the saved frames.",
                Location = new Point(16, 116), Size = new Size(422, 57)
            });
            var open = new Button { Name = "OpenTrainingMode", Text = "Open capture and review", Location = new Point(16, 188), Size = new Size(422, 36) };
            open.Click += (sender, args) => OpenTrainingMode();
            trainingPage.Controls.Add(open);
            trainingPage.Controls.Add(new Label
            {
                Text = "Review shortcuts (only in the review window):\n\nY  Loading text is present; advance one frame\nN  Loading text is absent; advance one frame\nR  Go back one frame\nU  Current frame + next 99 contain no loading text; advance 100\n\nLabels save automatically. Open a saved session to resume.\nExport training CSV includes only frames you have labeled.",
                Location = new Point(16, 244), Size = new Size(422, 172)
            });
            trainingModeStatus = new Label
            {
                Text = "Sessions are stored in Components/ML/training.\nCapture at least three separate recordings for train/validation/test; merge exported CSVs with prepare_captures.py.",
                Location = new Point(16, 434), Size = new Size(422, 65)
            };
            trainingPage.Controls.Add(trainingModeStatus);
        }

        private void OpenTrainingMode()
        {
            if (trainingForm != null && !trainingForm.IsDisposed)
            {
                if (trainingForm.WindowState == FormWindowState.Minimized) trainingForm.WindowState = FormWindowState.Normal;
                trainingForm.Show();
                trainingForm.Activate();
                return;
            }
            if (platform != "ENG/PS2" && platform != ExperimentalMlPlatform)
            {
                trainingModeStatus.Text = "Select ENG/PS2 or ENG/PS2 experimental ML on Setup before collecting training frames.";
                trainingModeStatus.ForeColor = Color.Firebrick;
                return;
            }
            try
            {
                var document = new XmlDocument();
                document.AppendChild(GetSettings(document));
                string root = Path.Combine(VulkanLoadingClassifier.ComponentsDirectory, "ML", "training");
                trainingForm = new TrainingCaptureForm(() =>
                {
                    if (closingTrainingSettings || IsDisposed) throw new ObjectDisposedException("Capture settings");
                    return CaptureImage();
                }, root, document.OuterXml, SetTrainingCaptureActive);
                trainingForm.FormClosed += TrainingFormClosed;
                // Freeze capture source/crop for this recording. The training window
                // remains modeless so the game can keep receiving normal keyboard input.
                foreach (TabPage page in tabControl1.TabPages)
                {
                    if (page == trainingPage) continue;
                    trainingDisabledControls[page] = page.Enabled;
                    page.Enabled = false;
                }
                trainingOwner = FindForm();
                if (trainingOwner != null)
                {
                    trainingOwner.FormClosing += TrainingOwnerClosing;
                    trainingForm.Show(trainingOwner);
                }
                else trainingForm.Show();
                trainingModeStatus.Text = "Training window open. Configure the target/rate, then press Start capture. Capture settings stay fixed until this window closes.";
                trainingModeStatus.ForeColor = SystemColors.ControlText;
            }
            catch (Exception ex)
            {
                CloseTrainingMode();
                RestoreTrainingControls();
                trainingModeStatus.Text = "Cannot open training mode: " + ex.Message;
                trainingModeStatus.ForeColor = Color.Firebrick;
            }
        }

        private void SetTrainingCaptureActive(bool active)
        {
            if (TrainingCaptureActive == active) return;
            TrainingCaptureActive = active;
            if (TrainingCaptureStateChanged != null) TrainingCaptureStateChanged(this, EventArgs.Empty);
        }

        private void TrainingOwnerClosing(object sender, FormClosingEventArgs args) { CloseTrainingMode(); }

        private void TrainingFormClosed(object sender, FormClosedEventArgs args)
        {
            SetTrainingCaptureActive(false);
            if (trainingOwner != null) trainingOwner.FormClosing -= TrainingOwnerClosing;
            trainingOwner = null;
            trainingForm = null;
            RestoreTrainingControls();
            if (!IsDisposed && !closingTrainingSettings)
                trainingModeStatus.Text = "Training window closed. Saved frames and labels can be reopened from Open session.";
        }

        private void RestoreTrainingControls()
        {
            foreach (var entry in trainingDisabledControls)
                if (!entry.Key.IsDisposed) entry.Key.Enabled = entry.Value;
            trainingDisabledControls.Clear();
        }

        public void CloseTrainingMode()
        {
            SetTrainingCaptureActive(false);
            if (trainingForm != null && !trainingForm.IsDisposed) trainingForm.CloseSafely();
        }

        private void DisposeTrainingMode()
        {
            closingTrainingSettings = true;
            CloseTrainingMode();
            if (trainingOwner != null) trainingOwner.FormClosing -= TrainingOwnerClosing;
            trainingOwner = null;
        }
    }
}
