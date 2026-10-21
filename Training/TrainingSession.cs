using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LiveSplit.CrashTWoCLoadRemover.Training
{
    /// <summary>A recording whose images and review transactions live on disk.</summary>
    public sealed class TrainingSession
    {
        private readonly object sync = new object();
        // Serialize disk mutations without making UI state reads wait for disk I/O.
        private readonly object writeSync = new object();
        private readonly List<int?> labels = new List<int?>();
        private int cursor;
        private bool complete;
        private string captureError;
        private long journalLength;
        private long sequence;
        private bool recording;

        private TrainingSession(string directory, string group, int target, int fps)
        {
            DirectoryPath = directory;
            Group = group;
            TargetFrames = target;
            FramesPerSecond = fps;
        }

        public string DirectoryPath { get; private set; }
        public string Group { get; private set; }
        public int TargetFrames { get; private set; }
        public int FramesPerSecond { get; private set; }
        public int FrameCount { get { lock (sync) return labels.Count; } }
        public int Cursor { get { lock (sync) return cursor; } }
        public bool Complete { get { lock (sync) return complete; } }
        public string CaptureError { get { lock (sync) return captureError; } }
        public int LabeledCount
        {
            get
            {
                lock (sync)
                {
                    int count = 0;
                    foreach (int? label in labels) if (label.HasValue) count++;
                    return count;
                }
            }
        }

        public static TrainingSession Create(string rootDirectory, int targetFrames,
            int framesPerSecond, string captureSettingsXml)
        {
            if (targetFrames < 1)
                throw new ArgumentOutOfRangeException("targetFrames", "Choose at least one frame.");
            if (framesPerSecond < 1 || framesPerSecond > 30)
                throw new ArgumentOutOfRangeException("framesPerSecond", "Choose 1 to 30 FPS.");
            string root = Path.GetFullPath(rootDirectory);
            Directory.CreateDirectory(root);
            RejectReparsePoint(root);
            string group = "session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N");
            string directory = Path.Combine(root, group);
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(Path.Combine(directory, "frames"));
            var session = new TrainingSession(directory, group, targetFrames, framesPerSecond);
            var metadata = new XElement("trainingSession", new XAttribute("version", "1"),
                new XElement("group", group), new XElement("targetFrames", targetFrames),
                new XElement("framesPerSecond", framesPerSecond),
                new XElement("captureSettingsXml", captureSettingsXml ?? String.Empty));
            session.AtomicWrite("session.xml", delegate(Stream stream) { metadata.Save(stream); });
            session.SaveCaptureState(false, null);
            using (var stream = new FileStream(Path.Combine(directory, "review.journal"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read)) stream.Flush(true);
            return session;
        }

        public static TrainingSession Open(string directory)
        {
            string fullPath = Path.GetFullPath(directory);
            RejectReparsePoint(fullPath);
            RejectReparsePoint(Path.Combine(fullPath, "frames"));
            XElement metadata = ReadXml(Path.Combine(fullPath, "session.xml"));
            if (metadata.Name != "trainingSession" || (string)metadata.Attribute("version") != "1")
                throw new InvalidDataException("Unsupported training session metadata.");
            string group = RequiredElement(metadata, "group");
            int target = ParseInt(RequiredElement(metadata, "targetFrames"));
            int fps = ParseInt(RequiredElement(metadata, "framesPerSecond"));
            if (String.IsNullOrWhiteSpace(group) || group.IndexOfAny(new[] { '/', '\\', '\r', '\n', ',' }) >= 0
                || target < 1 || fps < 1 || fps > 30)
                throw new InvalidDataException("Invalid training session metadata.");
            RequiredElement(metadata, "captureSettingsXml");
            var session = new TrainingSession(fullPath, group, target, fps);
            string[] paths = Directory.GetFiles(Path.Combine(fullPath, "frames"));
            Array.Sort(paths, delegate(string left, string right)
            {
                // D6 filenames can grow beyond six digits in larger recordings.
                int length = left.Length.CompareTo(right.Length);
                return length != 0 ? length : StringComparer.Ordinal.Compare(left, right);
            });
            foreach (string path in paths)
            {
                // An interrupted temporary write is never a visible frame.
                if (Path.GetFileName(path).EndsWith(".tmp", StringComparison.Ordinal)) continue;
                int index = session.labels.Count;
                if (index >= target || Path.GetFileName(path) != FrameFileName(index))
                    throw new InvalidDataException("Frame files are missing, out of order, or invalid: " + path);
                RejectReparsePoint(path);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var image = Image.FromStream(stream, true, true))
                {
                    if (image.Width != 100 || image.Height != 100 || image.RawFormat.Guid != ImageFormat.Png.Guid)
                        throw new InvalidDataException("Expected a complete 100 x 100 PNG: " + path);
                    // Decode pixels now; do not retain GDI objects or file handles.
                    using (var decoded = new Bitmap(image)) decoded.GetPixel(99, 99);
                }
                session.labels.Add(null);
            }
            string capturePath = Path.Combine(fullPath, "capture.xml");
            if (File.Exists(capturePath))
            {
                XElement state = ReadXml(capturePath);
                bool savedComplete;
                if (state.Name != "capture" || !Boolean.TryParse(RequiredElement(state, "complete"), out savedComplete))
                    throw new InvalidDataException("Invalid capture status.");
                session.captureError = RequiredElement(state, "error");
                if (session.captureError.Length == 0) session.captureError = null;
                if (savedComplete && (session.labels.Count != target || session.captureError != null))
                    throw new InvalidDataException("Completed capture has missing frames or an error.");
                session.complete = savedComplete;
            }
            session.LoadJournal();
            return session;
        }

        public string GetFramePath(int index)
        {
            lock (sync)
            {
                CheckIndex(index);
                return Path.Combine(DirectoryPath, "frames", FrameFileName(index));
            }
        }

        public int? GetLabel(int index)
        {
            lock (sync) { CheckIndex(index); return labels[index]; }
        }

        public void AddFrame(Bitmap ownedByCaller)
        {
            if (ownedByCaller == null) throw new ArgumentNullException("ownedByCaller");
            if (ownedByCaller.Width != 100 || ownedByCaller.Height != 100)
                throw new ArgumentException("Capture must be an available 100 x 100 ROI.", "ownedByCaller");
            lock (writeSync)
            {
                int index;
                lock (sync)
                {
                    index = labels.Count;
                    if (index >= TargetFrames) throw new InvalidOperationException("The capture target is already reached.");
                }
                AtomicWrite(Path.Combine("frames", FrameFileName(index)),
                    delegate(Stream stream) { ownedByCaller.Save(stream, ImageFormat.Png); }, false);
                lock (sync)
                {
                    labels.Add(null);
                    complete = false;
                }
            }
        }

        public void FinishCapture(string error)
        {
            lock (writeSync)
            {
                string normalized = String.IsNullOrEmpty(error) ? null : error;
                bool finished;
                lock (sync) finished = labels.Count == TargetFrames && normalized == null;
                SaveCaptureState(finished, normalized);
                lock (sync)
                {
                    complete = finished;
                    captureError = normalized;
                }
            }
        }

        public void LabelCurrent(bool loading)
        {
            lock (writeSync)
            {
                int current;
                lock (sync)
                {
                    current = cursor;
                    if (current == labels.Count) return;
                }
                SaveReview(current + 1, current, 1, loading ? 1 : 0);
            }
        }

        public void LabelNextAbsent(int count)
        {
            if (count < 1) throw new ArgumentOutOfRangeException("count");
            lock (writeSync)
            {
                int current, actual;
                lock (sync)
                {
                    current = cursor;
                    actual = Math.Min(count, labels.Count - current);
                }
                if (actual == 0) return;
                SaveReview(current + actual, current, actual, 0);
            }
        }

        public void Previous()
        {
            lock (writeSync)
            {
                int current;
                lock (sync) current = cursor;
                if (current > 0) SaveReview(current - 1, 0, 0, 0);
            }
        }

        public void Seek(int newCursor)
        {
            lock (writeSync)
            {
                lock (sync)
                {
                    if (newCursor < 0 || newCursor > labels.Count) throw new ArgumentOutOfRangeException("newCursor");
                    if (newCursor == cursor) return;
                }
                SaveReview(newCursor, 0, 0, 0);
            }
        }

        public string ExportManifest()
        {
            lock (writeSync)
            {
                int?[] snapshot;
                lock (sync) snapshot = labels.ToArray();
                AtomicWrite("samples.csv", delegate(Stream stream)
                {
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, true))
                    {
                        writer.WriteLine("path,label,group");
                        for (int i = 0; i < snapshot.Length; i++)
                            if (snapshot[i].HasValue)
                                writer.WriteLine("frames/" + FrameFileName(i) + "," + snapshot[i].Value.ToString(CultureInfo.InvariantCulture)
                                    + "," + Csv(Group));
                    }
                });
                return Path.Combine(DirectoryPath, "samples.csv");
            }
        }

        internal IDisposable BeginRecording()
        {
            lock (writeSync)
            {
                lock (sync)
                    if (recording) throw new InvalidOperationException("This session already has a capture writer.");
                string lockPath = Path.Combine(DirectoryPath, "capture.lock");
                if (File.Exists(lockPath)) RejectReparsePoint(lockPath);
                var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    SaveCaptureState(false, null);
                    lock (sync)
                    {
                        recording = true;
                        complete = false;
                        captureError = null;
                    }
                    return lease;
                }
                catch { lease.Dispose(); throw; }
            }
        }

        internal void EndRecording(string error)
        {
            lock (sync)
            {
                recording = false;
                if (!String.IsNullOrEmpty(error)) { captureError = error; complete = false; }
            }
        }

        private void SaveCaptureState(bool finished, string error)
        {
            var state = new XElement("capture", new XElement("complete", finished), new XElement("error", error ?? String.Empty));
            AtomicWrite("capture.xml", delegate(Stream stream) { state.Save(stream); });
        }

        private void SaveReview(int nextCursor, int start, int count, int label)
        {
            string body = (sequence + 1).ToString(CultureInfo.InvariantCulture) + "|" + nextCursor.ToString(CultureInfo.InvariantCulture)
                + "|" + start.ToString(CultureInfo.InvariantCulture) + "|" + count.ToString(CultureInfo.InvariantCulture)
                + "|" + label.ToString(CultureInfo.InvariantCulture);
            byte[] bytes = Encoding.UTF8.GetBytes(body + "|" + Hash(body) + "\n");
            string path = Path.Combine(DirectoryPath, "review.journal");
            RejectReparsePoint(path);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                if (stream.Length != journalLength) throw new IOException("The review journal changed. Reopen the session before labeling.");
                stream.Position = journalLength;
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            journalLength += bytes.Length;
            sequence++;
            lock (sync)
            {
                for (int i = start; i < start + count; i++) labels[i] = label;
                cursor = nextCursor;
            }
        }

        private void LoadJournal()
        {
            string path = Path.Combine(DirectoryPath, "review.journal");
            RejectReparsePoint(path);
            byte[] bytes = File.ReadAllBytes(path);
            int end = bytes.Length;
            while (end > 0 && bytes[end - 1] != (byte)'\n') end--;
            string content = new UTF8Encoding(false, true).GetString(bytes, 0, end);
            foreach (string line in content.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Split('|');
                long nextSequence;
                if (parts.Length != 6 || !Int64.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out nextSequence)
                    || nextSequence != sequence + 1 || Hash(String.Join("|", parts, 0, 5)) != parts[5])
                    throw new InvalidDataException("The review journal is corrupt.");
                int nextCursor = ParseInt(parts[1]), start = ParseInt(parts[2]), count = ParseInt(parts[3]), label = ParseInt(parts[4]);
                if (nextCursor < 0 || nextCursor > labels.Count || start < 0 || count < 0
                    || start > labels.Count || count > labels.Count - start || (label != 0 && label != 1))
                    throw new InvalidDataException("The review journal contains an invalid frame index or label.");
                for (int i = start; i < start + count; i++) labels[i] = label;
                cursor = nextCursor;
                sequence = nextSequence;
            }
            if (end != bytes.Length)
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
                {
                    stream.SetLength(end);
                    stream.Flush(true);
                }
                captureError = "Recovered an interrupted review transaction; the last unsaved edit was ignored.";
                complete = false;
                SaveCaptureState(false, captureError);
            }
            journalLength = end;
        }

        private void AtomicWrite(string relativePath, Action<Stream> write, bool replace = true)
        {
            string destination = Path.Combine(DirectoryPath, relativePath);
            RejectReparsePoint(DirectoryPath);
            RejectReparsePoint(Path.GetDirectoryName(destination));
            if (File.Exists(destination)) RejectReparsePoint(destination);
            string temporary = null;
            bool ownsTemporary = false;
            try
            {
                using (var stream = CreateTemporary(Path.GetDirectoryName(destination), out temporary))
                {
                    ownsTemporary = true;
                    write(stream);
                    stream.Flush(true);
                }
                if (replace && File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static FileStream CreateTemporary(string directory, out string path)
        {
            for (int attempt = 0; ; attempt++)
            {
                // Independent short names leave room beneath the legacy Windows path limit.
                path = Path.Combine(directory, "." + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp");
                try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException)
                {
                    if (attempt >= 15 || !File.Exists(path)) throw;
                }
            }
        }

        private void CheckIndex(int index)
        {
            if (index < 0 || index >= labels.Count) throw new ArgumentOutOfRangeException("index");
        }

        private static string FrameFileName(int index)
        {
            return "frame" + index.ToString("D6", CultureInfo.InvariantCulture) + ".png";
        }

        private static string Csv(string value) { return "\"" + value.Replace("\"", "\"\"") + "\""; }

        private static string Hash(string value)
        {
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        private static int ParseInt(string value)
        {
            int parsed;
            if (!Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed))
                throw new InvalidDataException("Invalid integer in training session.");
            return parsed;
        }

        private static string RequiredElement(XElement element, string name)
        {
            XElement child = element.Element(name);
            if (child == null) throw new InvalidDataException("Missing session field: " + name);
            return child.Value;
        }

        private static XElement ReadXml(string path)
        {
            RejectReparsePoint(path);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings)) return XElement.Load(reader);
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Session paths cannot be symbolic links or reparse points: " + path);
        }
    }
}
