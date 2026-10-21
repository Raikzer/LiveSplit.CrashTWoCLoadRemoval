using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiveSplit.CrashTWoCLoadRemover.Training
{
    /// <summary>A modeless, disk-backed capture and keyboard review window.</summary>
    public sealed class TrainingCaptureForm : Form
    {
        private Func<Bitmap> _capture;
        private Action<bool> _captureStateChanged;
        private readonly string _rootDirectory;
        private readonly string _captureSettingsXml;
        private readonly Timer _captureTimer = new Timer();
        private readonly Timer _progressTimer = new Timer();
        private readonly Stopwatch _elapsed = new Stopwatch();
        private readonly NumericUpDown _target = new NumericUpDown();
        private readonly NumericUpDown _fps = new NumericUpDown();
        private readonly Button _start = new Button();
        private readonly Button _stop = new Button();
        private readonly Button _open = new Button();
        private readonly Button _export = new Button();
        private readonly Button _loading = new Button();
        private readonly Button _absent = new Button();
        private readonly Button _previous = new Button();
        private readonly Button _absentBatch = new Button();
        private readonly Label _sessionPath = new Label();
        private readonly Label _status = new Label();
        private readonly Label _frameInfo = new Label();
        private readonly Label _hints = new Label();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly PictureBox _preview = new PictureBox();
        private TrainingSession _session;
        private TrainingRecorder _recorder;
        private Task _stopTask;
        private bool _capturing;
        private bool _busy;
        private bool _dialogOpen;
        private bool _closeRequested;
        private bool _allowClose;
        private bool _imageReadable;
        private double _nextCaptureMilliseconds;

        public TrainingCaptureForm(Func<Bitmap> capture, string rootDirectory,
            string captureSettingsXml, Action<bool> captureStateChanged = null)
        {
            if (capture == null) throw new ArgumentNullException("capture");
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("Choose a capture directory.", "rootDirectory");
            _capture = capture;
            _rootDirectory = rootDirectory;
            _captureSettingsXml = captureSettingsXml;
            _captureStateChanged = captureStateChanged;

            Text = "Training capture and review";
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(920, 680);
            MinimumSize = new Size(740, 580);
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            BuildLayout();
            _captureTimer.Tick += CaptureTick;
            _progressTimer.Interval = 250;
            _progressTimer.Tick += delegate { UpdateCaptureProgress(); };
            _status.Text = "Choose a frame count and FPS, then start capture. You can minimize this window while playing.";
            _hints.Text = "Open an existing session to resume labeling. Labels and review position save automatically.";
            UpdateControls();
        }

        public bool IsCapturing { get { return _capturing; } }

        public void StopCapture()
        {
            BeginStop(null);
        }

        public void CloseSafely()
        {
            if (IsDisposed || _closeRequested) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(CloseSafely)); }
                catch (InvalidOperationException) { }
                return;
            }
            _closeRequested = true;
            _captureTimer.Stop();
            if (_recorder != null)
                BeginStop(null);
            else
            {
                _allowClose = true;
                Close();
            }
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 10
            };
            for (int row = 0; row < layout.RowCount; row++)
                layout.RowStyles.Add(new RowStyle(row == 6 ? SizeType.Percent : SizeType.AutoSize, row == 6 ? 100 : 0));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);
            layout.SizeChanged += delegate
            {
                int width = Math.Max(100, layout.ClientSize.Width - layout.Padding.Horizontal);
                foreach (var label in new[] { _sessionPath, _status, _frameInfo, _hints })
                    label.MaximumSize = new Size(width, 0);
            };

            var heading = MakeLabel("Capture the configured game crop, then label each saved frame.");
            layout.Controls.Add(heading, 0, 0);
            _sessionPath.AutoSize = true;
            _sessionPath.MaximumSize = new Size(1400, 0);
            _sessionPath.Margin = new Padding(0, 4, 0, 6);
            _sessionPath.Text = "No session open";
            layout.Controls.Add(_sessionPath, 0, 1);

            var options = MakeFlow();
            options.Controls.Add(MakeLabel("Frames:"));
            _target.Minimum = 1;
            _target.Maximum = 100000;
            _target.Value = 20000;
            _target.ThousandsSeparator = true;
            _target.Width = 100;
            _target.ValueChanged += delegate { UpdateStartText(); };
            options.Controls.Add(_target);
            options.Controls.Add(MakeLabel("FPS:"));
            _fps.Minimum = 1;
            _fps.Maximum = 30;
            _fps.Value = 30;
            _fps.Width = 65;
            options.Controls.Add(_fps);
            layout.Controls.Add(options, 0, 2);

            var actions = MakeFlow();
            SetupButton(_start, "Start capture", delegate { StartCapture(); });
            SetupButton(_stop, "Stop and review", delegate { StopCapture(); });
            SetupButton(_open, "Open session…", async delegate { await OpenSessionAsync(); });
            SetupButton(_export, "Export training CSV…", async delegate { await ExportAsync(); });
            actions.Controls.AddRange(new Control[] { _start, _stop, _open, _export });
            layout.Controls.Add(actions, 0, 3);
            UpdateStartText();

            _progress.Dock = DockStyle.Top;
            _progress.Height = 20;
            _progress.Margin = new Padding(0, 5, 0, 5);
            layout.Controls.Add(_progress, 0, 4);
            _status.AutoSize = true;
            _status.Dock = DockStyle.Fill;
            _status.Margin = new Padding(0, 3, 0, 8);
            layout.Controls.Add(_status, 0, 5);

            _preview.Dock = DockStyle.Fill;
            _preview.BackColor = Color.FromArgb(30, 30, 30);
            _preview.SizeMode = PictureBoxSizeMode.Zoom;
            _preview.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(_preview, 0, 6);
            _frameInfo.AutoSize = true;
            _frameInfo.Margin = new Padding(0, 3, 0, 3);
            layout.Controls.Add(_frameInfo, 0, 7);
            _hints.AutoSize = true;
            _hints.Margin = new Padding(0, 3, 0, 6);
            layout.Controls.Add(_hints, 0, 8);

            var review = MakeFlow();
            SetupButton(_loading, "Y — Loading", delegate { ApplyReviewKey(Keys.Y); });
            SetupButton(_absent, "N — Absent", delegate { ApplyReviewKey(Keys.N); });
            SetupButton(_previous, "R — Previous", delegate { ApplyReviewKey(Keys.R); });
            SetupButton(_absentBatch, "U — Next 100 absent", delegate { ApplyReviewKey(Keys.U); });
            review.Controls.AddRange(new Control[] { _loading, _absent, _previous, _absentBatch });
            layout.Controls.Add(review, 0, 9);
        }

        private static FlowLayoutPanel MakeFlow()
        {
            return new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        }

        private static Label MakeLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 7, 9, 5) };
        }

        private static void SetupButton(Button button, string text, EventHandler click)
        {
            button.Text = text;
            button.AutoSize = true;
            button.Padding = new Padding(5, 3, 5, 3);
            button.Margin = new Padding(0, 3, 8, 3);
            button.Click += click;
        }

        private void UpdateStartText()
        {
            _start.Text = string.Format("Start capture ({0:N0} frames)", _target.Value);
        }

        private void StartCapture()
        {
            if (_capturing || _busy || _closeRequested) return;
            try
            {
                var session = TrainingSession.Create(_rootDirectory, (int)_target.Value, (int)_fps.Value, _captureSettingsXml);
                var recorder = new TrainingRecorder(session);
                _session = session;
                _recorder = recorder;
                _stopTask = null;
                ClearPreview();
                _sessionPath.Text = session.DirectoryPath;
                _frameInfo.Text = "Recording the configured crop";
                _hints.Text = "Stop and review when finished. Review keys are inactive while recording.";
                _capturing = true;
                NotifyCaptureState(true);
                _elapsed.Restart();
                _nextCaptureMilliseconds = 0;
                _captureTimer.Interval = 15;
                _captureTimer.Start();
                _progressTimer.Start();
                UpdateControls();
                UpdateCaptureProgress();
            }
            catch (Exception ex)
            {
                if (_recorder != null) BeginStop(ex.Message);
                else ShowError("Could not start capture", ex.Message);
            }
        }

        // This delegate is deliberately called only by the WinForms timer on the UI thread.
        private void CaptureTick(object sender, EventArgs e)
        {
            var recorder = _recorder;
            if (!_capturing || recorder == null || _stopTask != null) return;
            if (!string.IsNullOrEmpty(recorder.Error))
            {
                BeginStop(recorder.Error);
                return;
            }
            if (recorder.AcceptedFrames >= _session.TargetFrames)
            {
                BeginStop(null);
                return;
            }
            // Do not capture (and allocate a Bitmap) when the PNG writer is full.
            if (!recorder.CanAccept) return;
            double now = _elapsed.Elapsed.TotalMilliseconds;
            if (now < _nextCaptureMilliseconds) return;
            // Fractional stopwatch deadlines avoid the 30.3 FPS of a 33 ms timer.
            // After a slow tick, resume the requested cadence without burst catch-up.
            double interval = 1000.0 / _session.FramesPerSecond;
            _nextCaptureMilliseconds = Math.Max(_nextCaptureMilliseconds + interval, now + interval * 0.5);
            Bitmap frame = null;
            try
            {
                frame = _capture();
                if (frame == null || frame.Width <= 1 || frame.Height <= 1)
                    throw new InvalidOperationException("The game crop is unavailable or too small. Check the capture settings and start a new session.");
                if (recorder.TryEnqueue(frame)) frame = null;
                if (recorder.AcceptedFrames >= _session.TargetFrames) BeginStop(null);
            }
            catch (Exception ex) { BeginStop(ex.Message); }
            finally { if (frame != null) frame.Dispose(); }
        }

        private void BeginStop(string captureError)
        {
            _captureTimer.Stop();
            if (_recorder == null || _stopTask != null) return;
            // Mark the operation before starting it: an already completed StopAsync may
            // otherwise run its continuation before the assignment has happened.
            _stopTask = Task.FromResult(0);
            _stopTask = StopAndReviewAsync(_recorder, captureError);
        }

        private async Task StopAndReviewAsync(TrainingRecorder recorder, string captureError)
        {
            UpdateControls();
            _status.Text = "Finishing accepted frames on disk…";
            string error = captureError;
            try
            {
                await recorder.StopAsync();
                if (!string.IsNullOrEmpty(recorder.Error)) error = CombineErrors(error, recorder.Error);
                if (!string.IsNullOrEmpty(error))
                    await Task.Run(() => _session.FinishCapture(error));
            }
            catch (Exception ex) { error = CombineErrors(error, ex.Message); }
            finally
            {
                recorder.Dispose();
                _recorder = null;
                _capturing = false;
                _elapsed.Stop();
                _progressTimer.Stop();
                NotifyCaptureState(false);
            }
            if (IsDisposed) return;
            if (_closeRequested)
            {
                _allowClose = true;
                Close();
                return;
            }
            RefreshReview();
            if (!string.IsNullOrEmpty(error))
            {
                _status.Text = "Capture stopped: " + error + " Saved frames remain available for review.";
                ShowError("Capture stopped", _status.Text);
            }
        }

        private static string CombineErrors(string first, string second)
        {
            if (string.IsNullOrEmpty(first)) return second;
            if (string.IsNullOrEmpty(second) || first == second) return first;
            return first + Environment.NewLine + second;
        }

        private void UpdateCaptureProgress()
        {
            if (_recorder == null || IsDisposed) return;
            int saved = _recorder.SavedFrames;
            int accepted = _recorder.AcceptedFrames;
            _progress.Maximum = Math.Max(1, _session.TargetFrames);
            _progress.Value = Math.Min(saved, _progress.Maximum);
            double seconds = _elapsed.Elapsed.TotalSeconds;
            double rate = seconds > 0 ? saved / seconds : 0;
            string remaining = rate > 0
                ? FormatDuration(TimeSpan.FromSeconds(Math.Max(0, (_session.TargetFrames - saved) / rate)))
                : "estimating";
            _status.Text = string.Format("{0:N0} / {1:N0} saved · {2:N0} accepted · {3:F1} saved FPS · elapsed {4} · remaining ~{5}{6}",
                saved, _session.TargetFrames, accepted, rate, FormatDuration(_elapsed.Elapsed), remaining,
                _stopTask != null ? " · finishing writes…" : "");
            if (_stopTask == null && (!string.IsNullOrEmpty(_recorder.Error) || accepted >= _session.TargetFrames))
                BeginStop(_recorder.Error);
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return string.Format("{0:00}:{1:00}:{2:00}", (int)duration.TotalHours, duration.Minutes, duration.Seconds);
        }

        private async Task OpenSessionAsync()
        {
            if (_busy || _closeRequested) return;
            if (_recorder != null)
            {
                StopCapture();
                await _stopTask;
                if (IsDisposed || _closeRequested) return;
            }
            string directory;
            _dialogOpen = true;
            try
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Select an existing training session folder";
                    dialog.ShowNewFolderButton = false;
                    dialog.SelectedPath = _session == null ? _rootDirectory : _session.DirectoryPath;
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    directory = dialog.SelectedPath;
                }
            }
            finally { _dialogOpen = false; }
            _busy = true;
            UpdateControls();
            _status.Text = "Opening session and checking saved frames…";
            try
            {
                var session = await Task.Run(() => TrainingSession.Open(directory));
                if (IsDisposed || _closeRequested) return;
                _session = session;
                _sessionPath.Text = session.DirectoryPath;
                RefreshReview();
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !_closeRequested) ShowError("Could not open session", ex.Message);
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) UpdateControls();
            }
        }

        private async Task ExportAsync()
        {
            if (_session == null || _capturing || _busy || _closeRequested) return;
            _busy = true;
            UpdateControls();
            _status.Text = "Exporting labeled frames…";
            try
            {
                var session = _session;
                string path = await Task.Run(() => session.ExportManifest());
                if (IsDisposed || _closeRequested) return;
                _status.Text = "Training CSV: " + path;
                _dialogOpen = true;
                try
                {
                    MessageBox.Show(this, "Exported labeled frames to:" + Environment.NewLine + path + Environment.NewLine + Environment.NewLine
                        + "Next: run ml/prepare_captures.py to merge and deduplicate session CSVs before training.",
                        "Training CSV exported", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                finally { _dialogOpen = false; }
            }
            catch (Exception ex)
            {
                if (!IsDisposed && !_closeRequested) ShowError("Could not export training CSV", ex.Message);
            }
            finally
            {
                _busy = false;
                if (!IsDisposed) UpdateControls();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool reviewKey = keyData == Keys.Y || keyData == Keys.N || keyData == Keys.R || keyData == Keys.U;
            if (reviewKey && CanReview() && ContainsFocus && ActiveForm == this && !InputHasFocus())
            {
                // WM_KEYDOWN's previous-state bit rejects held-key auto-repeat.
                if ((msg.LParam.ToInt64() & (1L << 30)) == 0) ApplyReviewKey(keyData);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private bool InputHasFocus()
        {
            Control control = ActiveControl;
            while (control != null)
            {
                if (control is TextBoxBase || control is NumericUpDown || control is ComboBox) return true;
                var container = control as ContainerControl;
                if (container == null) break;
                control = container.ActiveControl;
            }
            return false;
        }

        private bool CanReview()
        {
            return _session != null && !_capturing && !_busy && !_dialogOpen && !_closeRequested;
        }

        private void ApplyReviewKey(Keys key)
        {
            if (!CanReview()) return;
            try
            {
                if (key == Keys.R) _session.Previous();
                else if (_imageReadable && _session.Cursor < _session.FrameCount)
                {
                    if (key == Keys.Y) _session.LabelCurrent(true);
                    else if (key == Keys.N) _session.LabelCurrent(false);
                    else if (key == Keys.U) _session.LabelNextAbsent(100);
                    else return;
                }
                else return;
                RefreshReview();
            }
            catch (Exception ex)
            {
                // A failed durable edit must not appear successful in the UI.
                RefreshReview();
                _status.Text = "Review change was not saved: " + ex.Message;
                ShowError("Could not save review change", ex.Message);
            }
        }

        private void RefreshReview()
        {
            ClearPreview();
            if (_session == null) { UpdateControls(); return; }
            int count = _session.FrameCount;
            int cursor = _session.Cursor;
            _progress.Maximum = Math.Max(1, count);
            _progress.Value = Math.Min(_session.LabeledCount, _progress.Maximum);
            _status.Text = string.Format("{0:N0} / {1:N0} labeled · {2}", _session.LabeledCount, count,
                _session.Complete ? "capture complete" : "partial capture");
            if (!string.IsNullOrEmpty(_session.CaptureError)) _status.Text += " · " + _session.CaptureError;
            _hints.Text = "Y = loading + next · N = absent + next · R = previous · U = current + next 99 absent (advance 100). Autosaved.";
            if (cursor >= count)
            {
                _frameInfo.Text = count == 0 ? "This session has no saved frames." : "End of session. Press R to revisit the last frame, or export the labeled frames.";
            }
            else
            {
                int? label = _session.GetLabel(cursor);
                _frameInfo.Text = string.Format("Frame {0:N0} / {1:N0} · {2}", cursor + 1, count,
                    !label.HasValue ? "Unlabeled" : label.Value == 1 ? "Loading (Y)" : "Absent (N)");
                try
                {
                    // Clone before closing the stream so review never locks a PNG file.
                    using (var stream = File.OpenRead(_session.GetFramePath(cursor)))
                    using (var source = new Bitmap(stream))
                        _preview.Image = new Bitmap(source);
                    _imageReadable = true;
                }
                catch (Exception ex)
                {
                    _status.Text = "Cannot read this frame: " + ex.Message;
                    _frameInfo.Text += " · image unavailable; repair the file and reopen the session";
                }
            }
            UpdateControls();
        }

        private void ClearPreview()
        {
            var old = _preview.Image;
            _preview.Image = null;
            _imageReadable = false;
            if (old != null) old.Dispose();
        }

        private void UpdateControls()
        {
            bool idle = !_capturing && !_busy && !_closeRequested;
            _start.Enabled = idle;
            _target.Enabled = idle;
            _fps.Enabled = idle;
            _stop.Enabled = _capturing && _stopTask == null && !_closeRequested;
            _open.Enabled = idle;
            _export.Enabled = idle && _session != null;
            bool review = CanReview();
            _loading.Enabled = _absent.Enabled = _absentBatch.Enabled = review && _imageReadable;
            _previous.Enabled = review && _session.Cursor > 0;
        }

        private void NotifyCaptureState(bool active)
        {
            var callback = _captureStateChanged;
            if (callback == null) return;
            try { callback(active); }
            catch (Exception ex)
            {
                // A dying owner must not prevent this form from releasing its resources.
                if (active) throw;
                Debug.WriteLine("Could not restore capture owner state: " + ex);
            }
        }

        private void ShowError(string title, string message)
        {
            if (IsDisposed || _closeRequested) return;
            _dialogOpen = true;
            try { MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { _dialogOpen = false; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose && _recorder != null)
            {
                e.Cancel = true;
                CloseSafely();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closeRequested = true;
                _captureTimer.Stop();
                _progressTimer.Stop();
                if (_recorder != null && _stopTask == null)
                {
                    _recorder.StopAsync();
                    _recorder.Dispose();
                    _recorder = null;
                }
                _captureTimer.Dispose();
                _progressTimer.Dispose();
                ClearPreview();
                _capture = null;
                // Direct owner disposal cannot await. Capture has stopped; queued PNGs
                // finish in StopAndReviewAsync without retaining the capture delegate.
                if (_capturing)
                {
                    _capturing = false;
                    NotifyCaptureState(false);
                }
                _captureStateChanged = null;
            }
            base.Dispose(disposing);
        }
    }
}
