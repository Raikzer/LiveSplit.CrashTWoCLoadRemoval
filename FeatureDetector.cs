using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Drawing;
using System.IO;
using System.Drawing.Imaging;

namespace CrashTWoCLoadDetector
{
    //This class contains settings, features and methods for computing features from a given Bitmap
    internal class FeatureDetector
    {
        #region Public Methods

        public static bool ClearBackground(ref Bitmap capture)
        {
            bool special = false;
            Rectangle rect = new Rectangle(0, 0, capture.Width, capture.Height);
            BitmapData captureData =
            capture.LockBits(rect, ImageLockMode.ReadWrite,
            capture.PixelFormat);

            // Get the address of the first line.
            IntPtr ptr = captureData.Scan0;

            // Declare an array to hold the bytes of the bitmap.
            int bytes = Math.Abs(captureData.Stride) * captureData.Height;
            byte[] rgbValues = new byte[bytes];

            // Copy the RGB values into the array.
            System.Runtime.InteropServices.Marshal.Copy(ptr, rgbValues, 0, bytes);
            for (int i = 0; i < bytes - 3; i += 4)
            {

                if (rgbValues[i] > 45 || rgbValues[i + 1] < 110)
                {

                    //Console.WriteLine(rgbValues[i]);
                    //Console.WriteLine(rgbValues[i + 1]);
                    //Console.WriteLine(rgbValues[i + 2]);

                    rgbValues[i] = 255;
                    rgbValues[i + 1] = 255;
                    rgbValues[i + 2] = 255;
                }

                else
                {
                    rgbValues[i] = 0;
                    rgbValues[i + 1] = 0;
                    rgbValues[i + 2] = 0;

                    if (i > bytes / 2)
                    {
                        special = true;
                    }
                }

            }

            // Copy the RGB values back to the bitmap
            System.Runtime.InteropServices.Marshal.Copy(rgbValues, 0, ptr, bytes);
            capture.UnlockBits(captureData);
            return special;
        }

        public static int ClearBackgroundPostLoad(ref Bitmap capture)
        {
            int lowestBit = 0;
            Rectangle rect = new Rectangle(0, 0, capture.Width, capture.Height);
            BitmapData captureData =
            capture.LockBits(rect, ImageLockMode.ReadWrite,
            capture.PixelFormat);

            // Get the address of the first line.
            IntPtr ptr = captureData.Scan0;

            // Declare an array to hold the bytes of the bitmap.
            int bytes = Math.Abs(captureData.Stride) * captureData.Height;
            byte[] rgbValues = new byte[bytes];

            // Copy the RGB values into the array.
            System.Runtime.InteropServices.Marshal.Copy(ptr, rgbValues, 0, bytes);
            for (int i = 0; i < bytes - 3; i += 4)
            {

                if (rgbValues[i] > 40 || rgbValues[i + 1] < 50 || rgbValues[i + 2] < 190)
                {
                    //Console.WriteLine(rgbValues[i]);
                    //Console.WriteLine(rgbValues[i + 1]);
                    //Console.WriteLine(rgbValues[i + 2]);

                    rgbValues[i] = 255;
                    rgbValues[i + 1] = 255;
                    rgbValues[i + 2] = 255;
                }
                else
                {
                    rgbValues[i] = 0;
                    rgbValues[i + 1] = 0;
                    rgbValues[i + 2] = 0;
                    lowestBit = i;
                }

            }

            // Copy the RGB values back to the bitmap
            System.Runtime.InteropServices.Marshal.Copy(rgbValues, 0, ptr, bytes);
            capture.UnlockBits(captureData);
            return lowestBit;
        }

        public static bool IsBlackScreen(ref Bitmap capture, int blacklevel = 1)
        {
            Rectangle rect = new Rectangle(0, 0, capture.Width, capture.Height);
            BitmapData captureData =
            capture.LockBits(rect, ImageLockMode.ReadOnly,
            capture.PixelFormat);
            // Get the address of the first line.
            IntPtr ptr = captureData.Scan0;

            // Declare an array to hold the bytes of the bitmap.
            int bytes = Math.Abs(captureData.Stride) * captureData.Height;
            byte[] rgbValues = new byte[bytes];

            // Copy the RGB values into the array.
            System.Runtime.InteropServices.Marshal.Copy(ptr, rgbValues, 0, bytes);
            for (int i = bytes - 2; i >= 0; i -= 4)
            {
                if (rgbValues[i] > blacklevel || rgbValues[i - 1] > blacklevel || rgbValues[i - 2] > blacklevel)
                {
                    capture.UnlockBits(captureData);
                    return false;
                }
            }
            capture.UnlockBits(captureData);
            return true;
        }

        public static int GetBlackLevel(ref Bitmap capture)
        {
            Rectangle rect = new Rectangle(0, 0, capture.Width, capture.Height);
            BitmapData captureData =
            capture.LockBits(rect, ImageLockMode.ReadOnly,
            capture.PixelFormat);
            // Get the address of the first line.
            IntPtr ptr = captureData.Scan0;

            // Declare an array to hold the bytes of the bitmap.
            int bytes = Math.Abs(captureData.Stride) * captureData.Height;
            byte[] rgbValues = new byte[bytes];

            // Copy the RGB values into the array.
            System.Runtime.InteropServices.Marshal.Copy(ptr, rgbValues, 0, bytes);
            int blacklevel = rgbValues[bytes - 2] + 1;
            for (int i = bytes - 2; i >= 0; i -= 4)
            {
                if (rgbValues[i] > blacklevel || rgbValues[i - 1] > blacklevel || rgbValues[i - 2] > blacklevel)
                {
                    capture.UnlockBits(captureData);
                    return -1;
                }
            }
            capture.UnlockBits(captureData);
            return blacklevel;
        }

        public static bool IsEndOfSpecialLoad(ref Bitmap capture, int blacklevel = 1)
        {
            Rectangle rect = new Rectangle(0, 0, capture.Width, capture.Height);
            BitmapData captureData =
            capture.LockBits(rect, ImageLockMode.ReadOnly,
            capture.PixelFormat);
            // Get the address of the first line.
            IntPtr ptr = captureData.Scan0;

            // Declare an array to hold the bytes of the bitmap.
            int bytes = Math.Abs(captureData.Stride) * captureData.Height;
            byte[] rgbValues = new byte[bytes];

            // Copy the RGB values into the array.
            System.Runtime.InteropServices.Marshal.Copy(ptr, rgbValues, 0, bytes);
            for (int i = 0; i < (bytes / 2) - 3; i += 4)
            {
                if (rgbValues[i] > blacklevel || rgbValues[i + 1] > blacklevel || rgbValues[i + 2] > blacklevel)
                {
                    capture.UnlockBits(captureData);
                    return true;
                }
            }
            capture.UnlockBits(captureData);
            return false;
        }

        //this is just for debugging
        public static string ToLiteral(string input)

        {
            using (var writer = new StringWriter())
            {
                using (var provider = CodeDomProvider.CreateProvider("CSharp"))
                {
                    provider.GenerateCodeFromExpression(new CodePrimitiveExpression(input), writer, null);
                    return writer.ToString();
                }
            }
        }
        #endregion Public Methods
    }
}