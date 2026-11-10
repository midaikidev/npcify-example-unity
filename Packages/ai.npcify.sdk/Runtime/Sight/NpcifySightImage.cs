using System;

namespace Npcify.Sdk.Sight
{
    /// <summary>The frame size and the motion grid of sight, as in the web SDK (frame-source.ts).</summary>
    public static class NpcifySightImage
    {
        /// <summary>
        /// The size that fits inside the capture size (640 x 360) with the same aspect ratio. A
        /// picture that is smaller keeps its size.
        /// </summary>
        public static void FitCaptureSize(int width, int height, out int fitWidth, out int fitHeight)
        {
            FitSize(width, height, NpcifySightLimits.CaptureWidth, NpcifySightLimits.CaptureHeight, out fitWidth, out fitHeight);
        }

        /// <summary>The size that fits inside a limit with the same aspect ratio, without an upscale.</summary>
        public static void FitSize(int width, int height, int limitWidth, int limitHeight, out int fitWidth, out int fitHeight)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var scale = Math.Min(1.0, Math.Min(limitWidth / (double)width, limitHeight / (double)height));
            fitWidth = Math.Max(1, RoundHalfUp(width * scale));
            fitHeight = Math.Max(1, RoundHalfUp(height * scale));
        }

        /// <summary>The luma (ITU-R BT.601, 0 to 255) of one pixel.</summary>
        public static byte Luma(byte red, byte green, byte blue)
        {
            return (byte)RoundHalfUp(0.299 * red + 0.587 * green + 0.114 * blue);
        }

        /// <summary>The mean absolute difference of two luma grids, 0 to 255.</summary>
        public static double MeanLumaChange(byte[] previous, byte[] next)
        {
            if (previous == null || next == null)
            {
                return 0.0;
            }
            var length = Math.Min(previous.Length, next.Length);
            if (length == 0)
            {
                return 0.0;
            }
            long sum = 0;
            for (var index = 0; index < length; index++)
            {
                sum += Math.Abs(next[index] - previous[index]);
            }
            return sum / (double)length;
        }

        // Math.round of JavaScript: .5 rounds up.
        private static int RoundHalfUp(double value)
        {
            return (int)Math.Floor(value + 0.5);
        }
    }
}
