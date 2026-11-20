using System;
using System.IO;
using Jellyfin.Plugin.MissingEpisodeMarker.Configuration;
using SkiaSharp;

namespace Jellyfin.Plugin.MissingEpisodeMarker.Posters;

/// <summary>
/// Draws the "missing episodes" marker onto a poster image with SkiaSharp
/// (the same graphics library Jellyfin itself uses).
/// </summary>
public static class PosterRenderer
{
    private static readonly float[] GrayscaleMatrix =
    {
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0.2126f, 0.7152f, 0.0722f, 0, 0,
        0,       0,       0,       1, 0,
    };

    // Shared for the lifetime of the server; never disposed.
    private static readonly Lazy<SKTypeface> BoldTypeface = new(() =>
        SKTypeface.FromFamilyName("sans-serif", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
        ?? SKTypeface.Default);

    /// <summary>
    /// Renders a marked copy of a poster.
    /// </summary>
    /// <param name="inputPath">The original poster.</param>
    /// <param name="outputPath">Where to write the marked JPEG.</param>
    /// <param name="config">Appearance settings.</param>
    /// <param name="text">Banner text (already formatted).</param>
    /// <returns>The image width and height.</returns>
    /// <exception cref="InvalidDataException">If the image cannot be decoded.</exception>
    public static (int Width, int Height) Render(string inputPath, string outputPath, PluginConfiguration config, string text)
    {
        using var source = SKBitmap.Decode(inputPath)
            ?? throw new InvalidDataException("Could not decode image: " + inputPath);

        using var output = DrawMarked(source, config, text);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = outputPath + ".tmp";
        using (var image = SKImage.FromBitmap(output))
        using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 92))
        using (var stream = File.Create(tempPath))
        {
            data.SaveTo(stream);
        }

        File.Move(tempPath, outputPath, true);
        return (output.Width, output.Height);
    }

    /// <summary>
    /// Renders a preview with the given settings, on a real poster if one is given, otherwise on a sample.
    /// </summary>
    /// <param name="posterPath">Optional poster to preview on.</param>
    /// <param name="config">Appearance settings.</param>
    /// <param name="text">Banner text.</param>
    /// <returns>JPEG bytes.</returns>
    public static byte[] RenderPreview(string? posterPath, PluginConfiguration config, string text)
    {
        SKBitmap? source = null;
        if (!string.IsNullOrEmpty(posterPath) && File.Exists(posterPath))
        {
            source = SKBitmap.Decode(posterPath);
        }

        source ??= CreateSamplePoster();
        try
        {
            using var output = DrawMarked(source, config, text);
            using var image = SKImage.FromBitmap(output);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
            return data.ToArray();
        }
        finally
        {
            source.Dispose();
        }
    }

    private static SKBitmap CreateSamplePoster()
    {
        var bitmap = new SKBitmap(400, 600);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(400, 600),
                new[] { new SKColor(0x3A, 0x1C, 0x8F), new SKColor(0xF1, 0x07, 0xA3) },
                null,
                SKShaderTileMode.Clamp),
        };
        canvas.DrawRect(new SKRect(0, 0, 400, 600), paint);
        return bitmap;
    }

    private static SKBitmap DrawMarked(SKBitmap source, PluginConfiguration config, string text)
    {
        var width = source.Width;
        var height = source.Height;

        var output = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.Black);

            using (var sourceImage = SKImage.FromBitmap(source))
            using (var imagePaint = new SKPaint { IsAntialias = true })
            {
                if (config.Grayscale)
                {
                    imagePaint.ColorFilter = SKColorFilter.CreateColorMatrix(GrayscaleMatrix);
                }

                canvas.DrawImage(sourceImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), imagePaint);
            }

            if (config.FilterEnabled && config.FilterOpacity > 0)
            {
                using var filterPaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = ParseColor(config.FilterColor, SKColors.Black).WithAlpha(ToAlpha(config.FilterOpacity)),
                };
                canvas.DrawRect(new SKRect(0, 0, width, height), filterPaint);
            }

            if (config.BannerEnabled)
            {
                DrawBanner(canvas, width, height, config, text);
            }
        }

        return output;
    }

    private static void DrawBanner(SKCanvas canvas, int width, int height, PluginConfiguration config, string text)
    {
        var bannerHeight = Math.Max(12f, height * Math.Clamp(config.BannerSizePercent, 3, 40) / 100f);

        using var bannerPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            Color = ParseColor(config.BannerColor, new SKColor(0xC6, 0x28, 0x28)).WithAlpha(ToAlpha(config.BannerOpacity)),
        };

        switch (config.BannerPosition)
        {
            case BannerPosition.Top:
                DrawStrip(canvas, new SKPoint(width / 2f, bannerHeight / 2f), width, bannerHeight, 0, bannerPaint, config, text);
                break;

            case BannerPosition.Diagonal:
            {
                // From bottom-left to top-right, through the centre.
                var angle = (float)(-Math.Atan2(height, width) * 180 / Math.PI);
                var length = (float)Math.Sqrt((width * (double)width) + (height * (double)height));
                DrawStrip(canvas, new SKPoint(width / 2f, height / 2f), length, bannerHeight, angle, bannerPaint, config, text);
                break;
            }

            case BannerPosition.CornerRibbon:
            {
                var offset = width * 0.22f;
                var length = width * 0.75f;
                var ribbonHeight = Math.Min(bannerHeight, width * 0.14f);
                DrawStrip(canvas, new SKPoint(width - offset, offset), length, ribbonHeight, 45, bannerPaint, config, text);
                break;
            }

            default: // Bottom
                DrawStrip(canvas, new SKPoint(width / 2f, height - (bannerHeight / 2f)), width, bannerHeight, 0, bannerPaint, config, text);
                break;
        }
    }

    private static void DrawStrip(
        SKCanvas canvas,
        SKPoint center,
        float length,
        float thickness,
        float angleDegrees,
        SKPaint bannerPaint,
        PluginConfiguration config,
        string text)
    {
        canvas.Save();
        canvas.Translate(center.X, center.Y);
        if (angleDegrees != 0)
        {
            canvas.RotateDegrees(angleDegrees);
        }

        canvas.DrawRect(new SKRect(-length / 2f, -thickness / 2f, length / 2f, thickness / 2f), bannerPaint);

        if (!string.IsNullOrWhiteSpace(text))
        {
            using var font = new SKFont { Typeface = BoldTypeface.Value, Size = thickness * 0.55f };
            using var textPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = ParseColor(config.TextColor, SKColors.White),
            };

            // Shrink the text until it fits inside the strip with some padding.
            var maxWidth = length * 0.86f;
            var textWidth = font.MeasureText(text);
            if (textWidth > maxWidth && textWidth > 0)
            {
                font.Size *= maxWidth / textWidth;
                textWidth = font.MeasureText(text);
            }

            // Vertically centre using the font metrics (Ascent is negative).
            var metrics = font.Metrics;
            var baseline = -(metrics.Ascent + metrics.Descent) / 2f;
            canvas.DrawText(text, -textWidth / 2f, baseline, font, textPaint);
        }

        canvas.Restore();
    }

    private static byte ToAlpha(int percent) => (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0);

    private static SKColor ParseColor(string? hex, SKColor fallback)
        => !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex.Trim(), out var color) ? color : fallback;
}
