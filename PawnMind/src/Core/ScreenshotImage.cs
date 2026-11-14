using System;

namespace PawnMind.Core
{
    public struct ScreenshotSize
    {
        public int Width { get; private set; }
        public int Height { get; private set; }

        public ScreenshotSize(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Pure image geometry for evidence captures. The Unity side owns the actual
    /// ScreenCapture/RenderTexture work; this type is unit-testable offline.
    /// </summary>
    public static class ScreenshotImage
    {
        public const int ShortSide = 768;
        public const int JpegQuality = 80;

        public static bool NeedsScale(int width, int height)
        {
            return width > 0 && height > 0 && Math.Min(width, height) > ShortSide;
        }

        public static ScreenshotSize ScaleForShortSide(int width, int height)
        {
            return ScaleForShortSide(width, height, ShortSide);
        }

        /// <summary>
        /// Downscale only: the shorter edge becomes <paramref name="shortSide"/>
        /// while the aspect ratio is preserved. Smaller images are returned as-is.
        /// </summary>
        public static ScreenshotSize ScaleForShortSide(int width, int height, int shortSide)
        {
            if (width <= 0 || height <= 0) return new ScreenshotSize(width, height);

            int shortEdge = Math.Min(width, height);
            if (shortSide <= 0 || shortEdge <= shortSide)
                return new ScreenshotSize(width, height);

            double scale = (double)shortSide / shortEdge;
            int scaledWidth = (int)Math.Round(width * scale, MidpointRounding.AwayFromZero);
            int scaledHeight = (int)Math.Round(height * scale, MidpointRounding.AwayFromZero);

            return new ScreenshotSize(
                Math.Max(1, scaledWidth),
                Math.Max(1, scaledHeight));
        }
    }
}
