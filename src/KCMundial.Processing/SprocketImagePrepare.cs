using SkiaSharp;

namespace KCMundial.Processing;

/// <summary>Prepara una imagen para imprimir en HP Sprocket 200 (2"×3"): encuadra dentro del tamaño sin recortar, con márgenes blancos y buena definición/color (SkiaSharp).</summary>
public static class SprocketImagePrepare
{
    /// <summary>Tamaño de salida para Sprocket 200 (2"×3") a 2× resolución para mejor calidad.</summary>
    private const int OutWidth = 636;   // 2 × 318
    private const int OutHeight = 848;  // 2 × 424
    private const double ContentFraction = 0.88;
    /// <summary>Desplazamiento 1 mm a la izquierda para margen derecho en impresora (en px a 2×).</summary>
    private static readonly int RightMarginOffsetPx = (int)Math.Round(OutWidth / (2.0 * 25.4) * 1.0);

    /// <summary>Genera una imagen 2"×3" (636×848) con la foto encajada dentro (sin recortar), fondos blancos y colores preservados. Devuelve ruta a JPEG temporal o null.</summary>
    public static string? Prepare(string sourceImagePath)
    {
        if (string.IsNullOrEmpty(sourceImagePath) || !File.Exists(sourceImagePath))
            return null;
        try
        {
            using var stream = File.OpenRead(sourceImagePath);
            using var source = SKBitmap.Decode(stream);
            if (source == null) return null;

            var contentW = (int)(OutWidth * ContentFraction);
            var contentH = (int)(OutHeight * ContentFraction);
            var scale = Math.Min((double)contentW / source.Width, (double)contentH / source.Height);
            var drawW = (int)Math.Round(source.Width * scale);
            var drawH = (int)Math.Round(source.Height * scale);
            var x = (OutWidth - drawW) / 2 - RightMarginOffsetPx;
            var y = (OutHeight - drawH) / 2;

            using var surface = SKSurface.Create(new SKImageInfo(OutWidth, OutHeight, SKColorType.Rgba8888, SKAlphaType.Opaque));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            var destRect = SKRect.Create(x, y, drawW, drawH);
            var srcRect = SKRect.Create(0, 0, source.Width, source.Height);
            using var paint = new SKPaint
            {
                FilterQuality = SKFilterQuality.High,
                IsAntialias = true,
                IsDither = true
            };
            canvas.DrawBitmap(source, srcRect, destRect, paint);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 98);
            var tempPath = Path.Combine(Path.GetTempPath(), "KCMundial_Sprocket_" + Guid.NewGuid().ToString("N") + ".jpg");
            using (var outStream = File.Create(tempPath))
                data.SaveTo(outStream);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }
}
