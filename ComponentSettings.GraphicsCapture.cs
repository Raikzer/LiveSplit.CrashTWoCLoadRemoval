using CrashTWoCLoadDetector;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LiveSplit.UI.Components
{
    public partial class CrashTWoCLoadRemovalSettings
    {
        private CheckBox useWindowsGraphicsCapture;
        private Label captureBackendStatus;
        private WindowsGraphicsCaptureProvider graphicsCaptureProvider;
        private bool applyingCaptureSettings;
        private bool refreshingCaptureSources;
        private string observedCaptureSource;
        private Size observedCaptureSize;
        private Timer capturePreviewRetryTimer;
        private bool capturePreviewRetryRequested;

        internal bool CaptureGeometryChanging { get; private set; }

        public bool UseWindowsGraphicsCapture { get { return useWindowsGraphicsCapture != null && useWindowsGraphicsCapture.Checked; } }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point point, uint flags);

        private void InitializeGraphicsCaptureSettings()
        {
            foreach (Control control in tabPage1.Controls)
                if (control.Top >= 88) control.Top += 56;
            tabPage1.AutoScrollMinSize = new Size(0, tabPage1.AutoScrollMinSize.Height + 56);
            useWindowsGraphicsCapture = new CheckBox
            {
                Name = "UseWindowsGraphicsCapture", Text = "Use Windows Graphics Capture",
                AutoSize = true, Location = new Point(30, 88)
            };
            captureBackendStatus = new Label
            {
                Name = "CaptureBackendStatus", Location = new Point(30, 112), Size = new Size(400, 30),
                Text = "GDI capture selected."
            };
            useWindowsGraphicsCapture.CheckedChanged += (sender, args) =>
            {
                ResetGraphicsCapture();
                UpdateCaptureBackendControls();
                NotifyDetectionConfigurationChanged();
                DrawPreview();
            };
            tabPage1.Controls.Add(useWindowsGraphicsCapture);
            tabPage1.Controls.Add(captureBackendStatus);
            UpdateCaptureBackendControls();
            capturePreviewRetryTimer = new Timer { Interval = 100 };
            capturePreviewRetryTimer.Tick += (sender, args) =>
            {
                capturePreviewRetryTimer.Stop();
                if (capturePreviewRetryRequested && CanRetryCapturePreview()) DrawPreview();
            };
            VisibleChanged += (sender, args) =>
            {
                if (!Visible) StopCapturePreviewRetry(false);
                else ResumePendingCapturePreview();
            };
            tabControl1.SelectedIndexChanged += (sender, args) =>
            {
                if (tabControl1.SelectedTab != tabPage1) StopCapturePreviewRetry(false);
                else ResumePendingCapturePreview();
            };
        }

        private void UpdateCaptureBackendControls()
        {
            trackBar1.Enabled = !UseWindowsGraphicsCapture;
            scalingLabel.Text = UseWindowsGraphicsCapture ? "Scaling: automatic (physical pixels)" : "Scaling: " + trackBar1.Value + "%";
            UpdateCaptureStatus(UseWindowsGraphicsCapture ? "Windows Graphics Capture selected." : "GDI capture selected.", false);
        }

        public void UpdateCaptureStatus(string message, bool error = false)
        {
            if (captureBackendStatus == null || IsDisposed) return;
            captureBackendStatus.Text = message;
            captureBackendStatus.ForeColor = error ? Color.Firebrick : SystemColors.ControlText;
        }

        private bool CanRetryCapturePreview()
        {
            return capturePreview && Visible && !IsDisposed && !applyingCaptureSettings
                && !refreshingCaptureSources && !TrainingCaptureActive && tabControl1.SelectedTab == tabPage1;
        }

        private void StopCapturePreviewRetry(bool clearRequest = true)
        {
            if (capturePreviewRetryTimer != null) capturePreviewRetryTimer.Stop();
            if (clearRequest) capturePreviewRetryRequested = false;
        }

        private void ResumePendingCapturePreview()
        {
            if (capturePreviewRetryRequested && capturePreviewRetryTimer != null && CanRetryCapturePreview())
                capturePreviewRetryTimer.Start();
        }

        private void RetryPendingCapturePreview()
        {
            capturePreviewRetryRequested = true;
            ResumePendingCapturePreview();
        }

        private void DisposeGraphicsCapture()
        {
            ResetGraphicsCapture();
            if (capturePreviewRetryTimer != null)
            {
                capturePreviewRetryTimer.Dispose();
                capturePreviewRetryTimer = null;
            }
        }

        public void ResetGraphicsCapture()
        {
            StopCapturePreviewRetry();
            if (graphicsCaptureProvider != null)
            {
                graphicsCaptureProvider.Dispose();
                graphicsCaptureProvider = null;
            }
            observedCaptureSource = null;
            observedCaptureSize = Size.Empty;
            ClearCapturePreview();
        }

        private void ClearCapturePreview()
        {
            // The diagnostic images also back the picture boxes, so dispose each only once.
            var images = new HashSet<Image>();
            if (previewPictureBox.Image != null) images.Add(previewPictureBox.Image);
            if (croppedPreviewPictureBox.Image != null) images.Add(croppedPreviewPictureBox.Image);
            if (previewImage != null) images.Add(previewImage);
            if (lastFullCapture != null) images.Add(lastFullCapture);
            if (lastFullCroppedCapture != null) images.Add(lastFullCroppedCapture);
            previewPictureBox.Image = null;
            croppedPreviewPictureBox.Image = null;
            previewImage = null;
            lastFullCapture = null;
            lastFullCroppedCapture = null;
            foreach (Image image in images) image.Dispose();
        }

        private WindowsCaptureTarget ResolveCaptureTarget(out Size legacySize)
        {
            if (applyingCaptureSettings) throw new CaptureNotReadyException("Waiting for capture settings.");
            legacySize = Size.Empty;
            if (processListComboBox.SelectedIndex < 0)
                throw new CaptureUnavailableException("The selected capture source is unavailable.");
            if (processCaptureIndex < 0)
            {
                int index = -processCaptureIndex - 1;
                if (index < 0 || index >= Screen.AllScreens.Length)
                    throw new CaptureUnavailableException("The selected display is unavailable.");
                Rectangle bounds = Screen.AllScreens[index].Bounds;
                legacySize = bounds.Size;
                IntPtr monitor = MonitorFromPoint(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), 0);
                if (monitor == IntPtr.Zero) throw new CaptureUnavailableException("The selected display is unavailable.");
                return new WindowsCaptureTarget(WindowsCaptureTargetKind.Monitor, monitor);
            }
            if (processList == null || processCaptureIndex >= processList.Length)
                throw new CaptureUnavailableException("The selected capture window is unavailable.");
            var process = processList[processCaptureIndex];
            process.Refresh();
            IntPtr handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero) throw new CaptureNotReadyException("Waiting for the selected capture window.");
            Rectangle client;
            if (DLLImportStuff.GetClientRect(handle, out client)) legacySize = client.Size;
            return new WindowsCaptureTarget(WindowsCaptureTargetKind.Window, handle);
        }

        private void ObserveCaptureSource(WindowsCaptureTarget target, Size size)
        {
            string key = (UseWindowsGraphicsCapture ? "WGC:" : "GDI:") + target.Kind + ":" + target.Handle;
            bool changed = observedCaptureSource != null && (observedCaptureSource != key || observedCaptureSize != size);
            observedCaptureSource = key;
            observedCaptureSize = size;
            if (changed)
            {
                ClearCapturePreview();
                CaptureGeometryChanging = true;
                try { NotifyDetectionConfigurationChanged(); }
                finally { CaptureGeometryChanging = false; }
            }
        }

        private Bitmap CaptureConfigured(ref ImageCaptureInfo info, CapturedFramePurpose purpose)
        {
            try
            {
                Size sourceSize;
                WindowsCaptureTarget target = ResolveCaptureTarget(out sourceSize);
                string sourceKey = (UseWindowsGraphicsCapture ? "WGC:" : "GDI:") + target.Kind + ":" + target.Handle;
                if (observedCaptureSource != null && observedCaptureSource != sourceKey)
                    ObserveCaptureSource(target, sourceSize);
                Bitmap result;
                if (UseWindowsGraphicsCapture)
                {
                    if (graphicsCaptureProvider == null) graphicsCaptureProvider = new WindowsGraphicsCaptureProvider();
                    using (Bitmap fullFrame = graphicsCaptureProvider.Capture(target))
                    {
                        ObserveCaptureSource(target, fullFrame.Size);
                        result = CapturedFrameProcessor.Process(fullFrame, ref info, selectionRectanglePreviewBox, purpose);
                    }
                }
                else
                {
                    ObserveCaptureSource(target, sourceSize);
                    if (purpose == CapturedFramePurpose.Detector)
                        result = CaptureImageLegacyWithInfo(ref info);
                    else result = CaptureImageFullPreviewLegacy(ref info, purpose == CapturedFramePurpose.CroppedPreview);
                }
                if (result == null || result.Width < 2 || result.Height < 2)
                {
                    if (result != null) result.Dispose();
                    throw new CaptureUnavailableException("The selected capture source is unavailable.");
                }
                UpdateCaptureStatus(UseWindowsGraphicsCapture ? "Windows Graphics Capture ready." : "GDI capture ready.");
                return result;
            }
            catch (CaptureNotReadyException ex) { ClearCapturePreview(); UpdateCaptureStatus(ex.Message); throw; }
            catch (CaptureUnavailableException ex) { ClearCapturePreview(); UpdateCaptureStatus(ex.Message, true); throw; }
            catch (Exception ex)
            {
                ClearCapturePreview();
                UpdateCaptureStatus("Capture unavailable: " + ex.Message, true);
                throw new CaptureUnavailableException("Capture unavailable: " + ex.Message, ex);
            }
        }

        private Bitmap CaptureImageLegacyWithInfo(ref ImageCaptureInfo info)
        {
            // Preserve the established GDI detector and post-load crop formulas.
            ImageCaptureInfo original = imageCaptureInfo;
            imageCaptureInfo = info;
            ImageCaptureInfo captured;
            Bitmap result;
            try
            {
                result = CaptureImageLegacy();
                captured = imageCaptureInfo;
            }
            finally { imageCaptureInfo = original; }
            info = captured;
            return result;
        }

        public Bitmap CaptureImage()
        {
            return CaptureConfigured(ref imageCaptureInfo, CapturedFramePurpose.Detector);
        }

        public Bitmap CaptureImagePostLoad()
        {
            ImageCaptureInfo info = imageCaptureInfo;
            info.cropOffsetX = 500.0f;
            info.cropOffsetY = -420.0f;
            info.captureSizeX = 400;
            info.captureSizeY = 200;
            info.resizeSizeXEng = 100;
            info.resizeSizeYEng = 50;
            return CaptureConfigured(ref info, CapturedFramePurpose.Detector);
        }

        public Bitmap CaptureImageFullPreview(ref ImageCaptureInfo info, bool useCrop = false)
        {
            return CaptureConfigured(ref info, useCrop ? CapturedFramePurpose.CroppedPreview : CapturedFramePurpose.FullPreview);
        }
    }
}
