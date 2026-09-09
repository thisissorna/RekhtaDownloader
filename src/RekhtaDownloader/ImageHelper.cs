using System;
using RekhtaDownloader.Models;
using SkiaSharp;

namespace RekhtaDownloader
{
    internal static class ImageHelper
    {
        public static SKImage RearrangeImage(SKImage source, PageData data)
        {
            var cellSize = 50;
            var borderWidth = 16;
            var sourceBitmap = SKBitmap.FromImage(source);
            // Create target bitmap with dimensions based on data.PageWidth and data.PageHeight
            var targetWidth = data.PageWidth > 0 ? data.PageWidth : sourceBitmap.Width;
            var targetHeight = data.PageHeight > 0 ? data.PageHeight : sourceBitmap.Height;
            var target = new SKBitmap(targetWidth, targetHeight, SKColorType.Rgb888x, SKAlphaType.Opaque);

            using (var canvas = new SKCanvas(target))
            {
                foreach (var sub in data.Sub)
                {
                    // Calculate source and target coordinates
                    int sourceX = (sub.X1 * cellSize) + (sub.X1 * borderWidth);
                    int sourceY = (sub.Y1 * cellSize) + (sub.Y1 * borderWidth);
                    int targetX = (sub.X2 * cellSize);
                    int targetY = (sub.Y2 * cellSize);

                    // Create source and target rectangles
                    var sourceRect = new SKRect(sourceX, sourceY, sourceX + cellSize + borderWidth,
                        sourceY + cellSize + borderWidth);
                    var targetRect = new SKRect(targetX, targetY, targetX + cellSize + borderWidth,
                        targetY + cellSize + borderWidth);

                    // Draw the image from source to target
                    canvas.DrawBitmap(sourceBitmap, sourceRect, targetRect);
                }

                // Ensure the canvas drawing operations are completed
                canvas.Flush();
            }

            return SKImage.FromBitmap(target); // Return the rearranged SKBitmap
        }

        public static SKImage ResizeImage(int scaleFactor, SKImage image)
        {
            int width = (int)(image.Width * ((double)scaleFactor / 10));
            int height = (int)(image.Height * ((double)scaleFactor / 10));
            using (var surface = SKSurface.Create(new SKImageInfo
            {
                Width = width,
                Height = height,
                ColorType = SKImageInfo.PlatformColorType,
                AlphaType = SKAlphaType.Opaque
            }))
            using (var paint = new SKPaint())
            {
                paint.IsAntialias = true;
                paint.FilterQuality = SKFilterQuality.High;

                surface.Canvas.DrawImage(image, new SKRectI(0, 0, width, height), paint);
                surface.Canvas.Flush();

                return surface.Snapshot();
            }
        }

        public static byte[] ToByteArray(this SKImage img, int quality = 90)
        {
            using (var bitmap = SKBitmap.FromImage(img))
            using (var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, quality))
            {
                if (data == null)
                {
                    throw new InvalidOperationException("Failed to encode image as JPEG.");
                }

                return data.ToArray();
            }
        }
    }
}