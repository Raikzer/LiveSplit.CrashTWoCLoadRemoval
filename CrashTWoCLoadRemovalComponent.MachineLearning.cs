using System;
using System.Drawing;
using System.IO;
using Tesseract;
using CrashTWoCLoadDetector;
using LiveSplit.CrashTWoCLoadRemover;

namespace LiveSplit.UI.Components
{
    partial class CrashTWoCLoadRemovalComponent
    {
        private VulkanLoadingClassifier mlClassifier;
        private string recognitionKey;
        private bool recognitionReady;
        private bool disposed;

        private bool IsMachineLearningPlatform
        {
            get { return settings.platform == CrashTWoCLoadRemovalSettings.ExperimentalMlPlatform; }
        }

        private void OnTrainingCaptureStateChanged(object sender, EventArgs args)
        {
            ResetRecognitionTransition();
        }

        private bool EnsureRecognitionBackend()
        {
            if (disposed) return false;
            string key = settings.platform + (IsMachineLearningPlatform ? "\n" + settings.MlModelPath : "");
            if (recognitionKey == key) return recognitionReady;
            bool switching = recognitionKey != null;
            if (switching) ResetRecognitionTransition();
            DisposeRecognitionBackend();
            recognitionKey = key;
            recognitionReady = false;
            try
            {
                if (IsMachineLearningPlatform)
                {
                    string model = VulkanLoadingClassifier.ResolveModelPath(settings.MlModelPath);
                    string shaders = Path.Combine(VulkanLoadingClassifier.ComponentsDirectory, "ML", "shaders");
                    mlClassifier = new VulkanLoadingClassifier(model, shaders, Environment.GetEnvironmentVariable("TWOC_ML_VALIDATION") == "1");
                    curLanguage = settings.platform;
                    settings.UpdateMlStatus(null, "Vulkan ready: " + mlClassifier.DeviceName + ". Use Test current capture for a confidence.");
                }
                else
                {
                    InitTesseract();
                    settings.UpdateMlStatus(null, "Select ENG/PS2 experimental ML on Setup to use Vulkan.");
                }
                recognitionReady = true;
                return true;
            }
            catch (Exception ex)
            {
                RecognitionFailed(ex);
                return false;
            }
        }

        private void DisposeRecognitionBackend()
        {
            if (mlClassifier != null) { mlClassifier.Dispose(); mlClassifier = null; }
            if (engine != null) { engine.Dispose(); engine = null; }
        }

        private void ResetRecognitionTransition()
        {
            isLoading = false;
            isBlackScreen = false;
            wasBlackScreen = false;
            waitOnFadeIn = false;
            waitOnLoad = false;
            waitFrames = 0;
            lowestBit = 0;
            specialLoad = false;
            isCmpFinished = false;
            settings.isCalibratingBlacklevel = false;
            settings.cmpBlackLevel = 100;
            if (timer != null) timer.CurrentState.IsGameTimePaused = false;
        }

        private void RecognitionFailed(Exception error)
        {
            recognitionReady = false; // Retry only after changing platform/path or clicking Reload model.
            ResetRecognitionTransition();
            settings.UpdateMlStatus(null, (IsMachineLearningPlatform ? "Vulkan ML unavailable: " : "Detector unavailable: ") + error.Message + " Use Reload model to retry.", true);
            DisposeRecognitionBackend();
        }

        private float PredictLoadingConfidence(Bitmap capture)
        {
            // Capture failures produce a 1x1 black fallback in the legacy capture code.
            // Never treat that as an ML prediction or a loading screen.
            if (capture.Width < 2 || capture.Height < 2) throw new InvalidOperationException("Capture source is unavailable.");
            float confidence = mlClassifier.Predict(capture);
            settings.UpdateMlStatus(confidence, "Vulkan: " + mlClassifier.DeviceName + ". A prediction is loading at the configured threshold.");
            return confidence;
        }

        private bool RecognizeLoadingText(Bitmap capture, bool calibration)
        {
            if (IsMachineLearningPlatform) return PredictLoadingConfidence(capture) >= settings.MlConfidenceThreshold;
            FeatureDetector.ClearBackground(ref capture, calibration ? settings.cmpBlackLevel : settings.blacklevel);
            using (Pix image = new BitmapToPixConverter().Convert(capture))
            using (var page = engine.Process(image, PageSegMode.SingleChar))
            {
                string text = page.GetText();
                string expected = settings.platform == "JPN/PS2" ? expectedResultJpn : settings.platform == "KRN/PS2" ? expectedResultKrn : expectedResultEng;
                int matches = 0;
                foreach (char character in expected) if (text.Contains(character.ToString())) matches++;
                if (settings.platform == "JPN/PS2") return matches > 3;
                if (settings.platform == "KRN/PS2") return matches > (calibration ? 2 : 1);
                return matches > 5 && text.Length == 8;
            }
        }

        private void OnMlPreviewRequested(object sender, EventArgs args)
        {
            if (!IsMachineLearningPlatform)
            {
                settings.UpdateMlStatus(null, "Select ENG/PS2 experimental ML on Setup first.");
                return;
            }
            if (!EnsureRecognitionBackend()) return;
            try { using (Bitmap capture = settings.CaptureImage()) PredictLoadingConfidence(capture); }
            catch (Exception ex) { RecognitionFailed(ex); }
        }

        private void OnMlReloadRequested(object sender, EventArgs args)
        {
            recognitionKey = null;
            ResetRecognitionTransition();
            EnsureRecognitionBackend();
        }
    }
}
