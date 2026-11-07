using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace CrashTWoCLoadDetector
{
    public enum CapturedFramePurpose { Detector, FullPreview, CroppedPreview }

    /// <summary>Applies the existing GDI crop/resize math to an owned physical-pixel frame.</summary>
    public static class CapturedFrameProcessor
    {
        public const int PreviewWidth = 400;
        public const int PreviewHeight = 160;

        /// <summary>
        /// Returns a new owned bitmap; the caller retains ownership of fullFrame.
        /// Selection coordinates are always relative to the 400 by 160 full preview.
        /// </summary>
        public static Bitmap Process(Bitmap fullFrame, ref ImageCaptureInfo info,
            Rectangle selectionRectangle, CapturedFramePurpose purpose)
        {
            if (fullFrame == null) throw new ArgumentNullException("fullFrame");
            if (purpose != CapturedFramePurpose.Detector && purpose != CapturedFramePurpose.FullPreview &&
                purpose != CapturedFramePurpose.CroppedPreview) throw new ArgumentOutOfRangeException("purpose");
            if (selectionRectangle.X < 0 || selectionRectangle.Y < 0 || selectionRectangle.Width <= 0 || selectionRectangle.Height <= 0 ||
                (long)selectionRectangle.X + selectionRectangle.Width > PreviewWidth ||
                (long)selectionRectangle.Y + selectionRectangle.Height > PreviewHeight)
                throw new CaptureUnavailableException("The capture selection is outside the preview.");

            ValidateConfiguration(info);
            // GDI full previews use twice the integer center, discarding an odd
            // final row/column. The selection must map to precisely that image.
            int fullWidth = 2 * (fullFrame.Width / 2);
            int fullHeight = 2 * (fullFrame.Height / 2);
            if (fullWidth <= 0 || fullHeight <= 0)
                throw new CaptureUnavailableException("The capture frame is too small.");
            info.crop_coordinate_left = selectionRectangle.Left * (fullWidth / (float)PreviewWidth);
            info.crop_coordinate_right = selectionRectangle.Right * (fullWidth / (float)PreviewWidth);
            info.crop_coordinate_top = selectionRectangle.Top * (fullHeight / (float)PreviewHeight);
            info.crop_coordinate_bottom = selectionRectangle.Bottom * (fullHeight / (float)PreviewHeight);

            bool selectedRegion = purpose != CapturedFramePurpose.FullPreview;
            // Use the original full dimensions for the initial GDI calculation;
            // SizeAdjustedCropAndOffset performs integer division for its center.
            int regionWidth = selectedRegion ? (int)(info.crop_coordinate_right - info.crop_coordinate_left) : fullFrame.Width;
            int regionHeight = selectedRegion ? (int)(info.crop_coordinate_bottom - info.crop_coordinate_top) : fullFrame.Height;
            if (regionWidth <= 0 || regionHeight <= 0)
                throw new CaptureUnavailableException("The selected capture region is too small.");
            ImageCapture.SizeAdjustedCropAndOffset(regionWidth, regionHeight, ref info);
            bool detector = purpose == CapturedFramePurpose.Detector;
            if (!detector)
            {
                info.actual_crop_size_x = 2 * info.center_of_frame_x;
                info.actual_crop_size_y = 2 * info.center_of_frame_y;
            }
            if (selectedRegion)
            {
                info.center_of_frame_x += info.crop_coordinate_left;
                info.center_of_frame_y += info.crop_coordinate_top;
            }

            float offsetX = detector ? info.actual_offset_x : 0;
            float offsetY = detector ? info.actual_offset_y : 0;
            float sourceX = info.center_of_frame_x + offsetX - info.actual_crop_size_x / 2;
            float sourceY = info.center_of_frame_y + offsetY - info.actual_crop_size_y / 2;
            if (!Finite(sourceX) || !Finite(sourceY) || !Finite(info.actual_crop_size_x) || !Finite(info.actual_crop_size_y) ||
                sourceX < int.MinValue || sourceX >= int.MaxValue || sourceY < int.MinValue || sourceY >= int.MaxValue ||
                info.actual_crop_size_x < 1 || info.actual_crop_size_x >= int.MaxValue ||
                info.actual_crop_size_y < 1 || info.actual_crop_size_y >= int.MaxValue)
                throw new CaptureUnavailableException("The detector crop has invalid dimensions.");
            var source = new Rectangle((int)sourceX, (int)sourceY, (int)info.actual_crop_size_x, (int)info.actual_crop_size_y);
            if (source.X < 0 || source.Y < 0 || (long)source.X + source.Width > fullFrame.Width ||
                (long)source.Y + source.Height > fullFrame.Height)
                throw new CaptureUnavailableException("The detector crop is outside the captured image.");

            int outputWidth = detector ? (info.resizeState == ResizeState.ENG ? info.resizeSizeXEng : info.resizeSizeXJpn) : info.captureSizeX;
            int outputHeight = detector ? (info.resizeState == ResizeState.ENG ? info.resizeSizeYEng : info.resizeSizeYJpn) : info.captureSizeY;
            if (outputWidth <= 0 || outputHeight <= 0 || (long)outputWidth * outputHeight > 256L * 1024 * 1024 / 4)
                throw new CaptureUnavailableException("The capture output has invalid dimensions.");
            using (Bitmap cropped = fullFrame.Clone(source, PixelFormat.Format32bppArgb))
                return ImageCapture.ResizeImage(cropped, outputWidth, outputHeight);
        }

        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }

        private static void ValidateConfiguration(ImageCaptureInfo info)
        {
            if (!Finite(info.featureVectorResolutionX) || info.featureVectorResolutionX <= 0 ||
                !Finite(info.featureVectorResolutionY) || info.featureVectorResolutionY <= 0 ||
                !Finite(info.captureAspectRatio) || info.captureAspectRatio <= 0 ||
                !Finite(info.cropOffsetX) || !Finite(info.cropOffsetY) || info.captureSizeX <= 0 || info.captureSizeY <= 0)
                throw new CaptureUnavailableException("The capture crop configuration is invalid.");
        }
    }
}
