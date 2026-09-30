using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;
using SkiaSharp;

namespace KCMundial.Processing;

public sealed class StickerComposer : IStickerComposer
{
    private readonly IPathResolver _pathResolver;
    private readonly IAppLogger? _logger;

    /// <summary>Returns the full output rect (photo = background, back on top). For overlay debug.</summary>
    public static (double x, double y, double w, double h) GetPhotoSlotRect(int outputWidth, int outputHeight)
    {
        return (0, 0, outputWidth, outputHeight);
    }

    public StickerComposer(IPathResolver pathResolver, IAppLogger? logger = null)
    {
        _pathResolver = pathResolver;
        _logger = logger;
    }

    /// <summary>
    /// Photo = full background (cover, face in upper third). Back from assets is drawn on top; output size = back size.
    /// </summary>
    public byte[] Compose(
        byte[] captureBgr,
        int captureWidth,
        int captureHeight,
        double faceCenterX,
        double faceCenterY,
        double faceHeightPx,
        double eyesY,
        int outputWidth,
        int outputHeight,
        int quality = 92,
        string? backImagePath = null)
    {
        var outW = (double)outputWidth;
        var outH = (double)outputHeight;

        // 1) Scale capture to cover full output size (UniformToFill)
        var scale = Math.Max(outW / captureWidth, outH / captureHeight);
        var scaledW = (int)Math.Round(captureWidth * scale);
        var scaledH = (int)Math.Round(captureHeight * scale);
        if (scaledW < 1) scaledW = 1;
        if (scaledH < 1) scaledH = 1;

        using var captureBitmap = BgrToSkBitmap(captureBgr, captureWidth, captureHeight);
        using var scaledBitmap = new SKBitmap(scaledW, scaledH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(scaledBitmap))
        {
            canvas.DrawBitmap(captureBitmap,
                SKRect.Create(0, 0, captureBitmap.Width, captureBitmap.Height),
                SKRect.Create(0, 0, scaledW, scaledH),
                new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true });
        }

        // 2) Crop outW x outH from scaled image centered on frame (matches preview / what you see)
        var cropCenterX = scaledW / 2.0;
        var cropCenterY = scaledH / 2.0;
        var cropLeft = cropCenterX - outW / 2.0;
        var cropTop = cropCenterY - outH / 2.0;
        var cropRight = cropLeft + outW;
        var cropBottom = cropTop + outH;

        if (cropTop < 0) { cropBottom -= cropTop; cropTop = 0; }
        if (cropBottom > scaledH) { cropTop -= (cropBottom - scaledH); cropBottom = scaledH; if (cropTop < 0) cropTop = 0; }
        if (cropLeft < 0) { cropRight -= cropLeft; cropLeft = 0; }
        if (cropRight > scaledW) { cropLeft -= (cropRight - scaledW); cropRight = scaledW; if (cropLeft < 0) cropLeft = 0; }

        cropTop = Math.Max(0, Math.Min(cropTop, scaledH - 1));
        cropBottom = Math.Min(scaledH, Math.Max(cropBottom, cropTop + 1));
        cropLeft = Math.Max(0, Math.Min(cropLeft, scaledW - 1));
        cropRight = Math.Min(scaledW, Math.Max(cropRight, cropLeft + 1));
        var cropWidth = cropRight - cropLeft;
        var cropHeight = cropBottom - cropTop;

        _logger?.Info($"StickerComposer: Photo=background(out={outputWidth}x{outputHeight}) scale={scale:F4} crop=({cropLeft:F0},{cropTop:F0},{cropWidth:F0},{cropHeight:F0})");

        var sourceRect = SKRect.Create((float)cropLeft, (float)cropTop, (float)cropWidth, (float)cropHeight);
        var destRect = SKRect.Create(0, 0, outputWidth, outputHeight);

        using var surface = SKSurface.Create(new SKImageInfo(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        var outputCanvas = surface.Canvas;

        // 3) Draw photo as full background
        outputCanvas.DrawBitmap(scaledBitmap, sourceRect, destRect,
            new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true });

        // 4) Draw back on top (alpha = transparent areas show photo)
        using var back = LoadBackground(_pathResolver, outputWidth, outputHeight, backImagePath);
        if (back != null)
            outputCanvas.DrawBitmap(back, 0, 0);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        return ms.ToArray();
    }

    private static SKBitmap BgrToSkBitmap(byte[] bgr, int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = bitmap.Pixels;
        for (var i = 0; i < width * height; i++)
        {
            var b = bgr[i * 3];
            var g = bgr[i * 3 + 1];
            var r = bgr[i * 3 + 2];
            pixels[i] = new SKColor((byte)r, (byte)g, (byte)b, 255);
        }
        bitmap.Pixels = pixels;
        return bitmap;
    }

    private static SKBitmap? LoadBackground(IPathResolver pathResolver, int outputWidth, int outputHeight, string? backImagePath = null)
    {
        var path = !string.IsNullOrEmpty(backImagePath) && File.Exists(backImagePath)
            ? backImagePath
            : (outputWidth <= 591 ? pathResolver.Back300Path : pathResolver.Back600Path);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        using var stream = File.OpenRead(path);
        using var bitmap = SKBitmap.Decode(stream);
        if (bitmap == null) return null;
        if (bitmap.Width != outputWidth || bitmap.Height != outputHeight)
        {
            var scaled = new SKBitmap(outputWidth, outputHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
            using (var c = new SKCanvas(scaled))
                c.DrawBitmap(bitmap, SKRect.Create(0, 0, outputWidth, outputHeight), new SKPaint { FilterQuality = SKFilterQuality.High });
            return scaled;
        }
        return bitmap.Copy();
    }

    private static byte[] EncodeToJpeg(SKBitmap bitmap, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        return ms.ToArray();
    }
}
