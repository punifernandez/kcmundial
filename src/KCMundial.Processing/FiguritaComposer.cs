using KCMundial.Core.Interfaces;
using SkiaSharp;

namespace KCMundial.Processing;

/// <summary>Tamaños de salida.</summary>
public static class OutputSizes
{
    /// <summary>Lado largo del máster (foto + marco). Con la figurita 5:7 queda en 2571×3600.</summary>
    public const int MasterLongSide = 3600;
    /// <summary>Figurita clásica 5×7 cm a 600 dpi (pantallas, QR, galería).</summary>
    public static readonly SKSizeI Figurita = new(1182, 1654);
    /// <summary>Ampliación 20×30 cm a 300 dpi.</summary>
    public static readonly SKSizeI Print20x30 = new(2362, 3543);
    public const int Dpi = 300;
}

/// <summary>
/// Arma la figurita: foto de la cámara (rotada y recortada al centro con la proporción del marco) con el marco encima.
/// Todo el trabajo de píxeles lo hace Skia; los marcos decodificados quedan en caché.
/// </summary>
public sealed class FiguritaComposer : IDisposable
{
    private readonly IAppLogger? _logger;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTime WriteTime, SKImage Image)> _frameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Proporción usada cuando no hay marco (figurita 5×7).</summary>
    public const double DefaultAspect = 5.0 / 7.0;

    public FiguritaComposer(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Crea la foto "derecha" a partir del cuadro BGRA de la cámara: aplica la rotación de montaje (0/90/180/270
    /// horario) y, si se pide, la espeja como el preview (antes de ponerle el marco, que queda siempre al derecho).
    /// </summary>
    public static SKImage CreateUprightPhoto(byte[] bgra, int width, int height, int rotationClockwise, bool mirror = false)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var source = SKImage.FromPixelCopy(info, bgra, width * 4)
            ?? throw new InvalidOperationException("No se pudo crear la imagen de la cámara.");

        var rotation = NormalizeRotation(rotationClockwise);
        if (rotation == 0 && !mirror)
            return source;

        var swap = rotation is 90 or 270;
        var outW = swap ? height : width;
        var outH = swap ? width : height;
        using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Opaque))
            ?? throw new InvalidOperationException("No se pudo crear la superficie para rotar.");
        var canvas = surface.Canvas;
        if (mirror)
            canvas.Scale(-1, 1, outW / 2f, 0); // espejo horizontal de la imagen ya derecha, igual que el preview
        canvas.Translate(outW / 2f, outH / 2f);
        canvas.RotateDegrees(rotation);
        canvas.Translate(-width / 2f, -height / 2f);
        canvas.DrawImage(source, 0, 0);
        canvas.Flush();
        source.Dispose();
        return surface.Snapshot();
    }

    public static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360 / 90 * 90;

    /// <summary>Proporción (ancho/alto) del marco, o 5:7 si no existe.</summary>
    public double GetFrameAspect(string? framePath)
    {
        var frame = GetFrame(framePath);
        return frame != null ? (double)frame.Width / frame.Height : DefaultAspect;
    }

    /// <summary>Máster en alta: foto cubriendo todo el lienzo (recorte centrado) + marco escalado encima.</summary>
    public SKImage ComposeMaster(SKImage photo, string? framePath)
    {
        var frame = GetFrame(framePath);
        var aspect = frame != null ? (double)frame.Width / frame.Height : DefaultAspect;
        var (w, h) = aspect <= 1
            ? ((int)Math.Round(OutputSizes.MasterLongSide * aspect), OutputSizes.MasterLongSide)
            : (OutputSizes.MasterLongSide, (int)Math.Round(OutputSizes.MasterLongSide / aspect));

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("No se pudo crear el lienzo del máster.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true, IsDither = true };
        canvas.DrawImage(photo, CoverSourceRect(photo.Width, photo.Height, w, h), SKRect.Create(w, h), paint);
        if (frame != null)
            canvas.DrawImage(frame, SKRect.Create(w, h), paint);
        canvas.Flush();

        _logger?.Info($"FiguritaComposer: master {w}x{h} from photo {photo.Width}x{photo.Height}, frame={(frame != null ? $"{frame.Width}x{frame.Height}" : "none")}");
        return surface.Snapshot();
    }

    /// <summary>Redimensiona (cubriendo, recorte centrado) y codifica a JPEG con 300 dpi en la cabecera.</summary>
    public static byte[] EncodeJpeg(SKImage image, SKSizeI size, int quality)
    {
        if (image.Width == size.Width && image.Height == size.Height)
            return EncodeJpeg(image, quality);

        using var surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("No se pudo crear el lienzo de salida.");
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true, IsDither = true };
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(image, CoverSourceRect(image.Width, image.Height, size.Width, size.Height), SKRect.Create(size.Width, size.Height), paint);
        surface.Canvas.Flush();
        using var resized = surface.Snapshot();
        return EncodeJpeg(resized, quality);
    }

    public static byte[] EncodeJpeg(SKImage image, int quality)
    {
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality)
            ?? throw new InvalidOperationException("No se pudo codificar el JPEG.");
        var bytes = data.ToArray();
        SetJfifDpi(bytes, OutputSizes.Dpi);
        return bytes;
    }

    /// <summary>
    /// Hoja para la impresora: la figurita entera (sin recortar) centrada en una hoja blanca del tamaño indicado,
    /// dejando un margen para lo que recorta la impresora al imprimir sin bordes.
    /// </summary>
    public static SKImage RenderPrintPage(SKImage figurita, double pageWidthInches, double pageHeightInches, double marginMm)
    {
        var w = (int)Math.Round(pageWidthInches * OutputSizes.Dpi);
        var h = (int)Math.Round(pageHeightInches * OutputSizes.Dpi);
        var margin = (float)(marginMm / 25.4 * OutputSizes.Dpi);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("No se pudo crear la hoja de impresión.");
        surface.Canvas.Clear(SKColors.White);
        var area = SKRect.Create(margin, margin, w - 2 * margin, h - 2 * margin);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true, IsDither = true };
        surface.Canvas.DrawImage(figurita, FitRect(figurita.Width, figurita.Height, area), paint);
        surface.Canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>
    /// La figurita entera en un lienzo de otra proporción (p. ej. 20×30), rellenando lo que sobra con el color
    /// del borde del marco (arriba/abajo o izquierda/derecha).
    /// </summary>
    public static SKImage RenderWithBands(SKImage figurita, SKSizeI size)
    {
        using var surface = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("No se pudo crear el lienzo.");
        var canvas = surface.Canvas;
        var dest = FitRect(figurita.Width, figurita.Height, SKRect.Create(size.Width, size.Height));
        var horizontal = dest.Height < size.Height; // sobra alto → bandas arriba y abajo
        var (first, second) = EdgeColors(figurita, horizontal);
        var firstHalf = horizontal ? SKRect.Create(0, 0, size.Width, size.Height / 2f) : SKRect.Create(0, 0, size.Width / 2f, size.Height);
        var secondHalf = horizontal ? SKRect.Create(0, size.Height / 2f, size.Width, size.Height / 2f) : SKRect.Create(size.Width / 2f, 0, size.Width / 2f, size.Height);
        using (var fill = new SKPaint { Color = first }) canvas.DrawRect(firstHalf, fill);
        using (var fill = new SKPaint { Color = second }) canvas.DrawRect(secondHalf, fill);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true, IsDither = true };
        canvas.DrawImage(figurita, dest, paint);
        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>Color promedio de los bordes opuestos (arriba/abajo o izquierda/derecha).</summary>
    private static (SKColor First, SKColor Second) EdgeColors(SKImage image, bool horizontalBands)
    {
        const int sample = 64;
        using var small = new SKBitmap(sample, sample, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(small))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
            canvas.DrawImage(image, SKRect.Create(sample, sample), paint);

        SKColor Average(Func<int, (int X, int Y)> at)
        {
            long r = 0, g = 0, b = 0;
            for (var i = 0; i < sample; i++)
            {
                var (x, y) = at(i);
                var c = small.GetPixel(x, y);
                r += c.Red; g += c.Green; b += c.Blue;
            }
            return new SKColor((byte)(r / sample), (byte)(g / sample), (byte)(b / sample));
        }

        return horizontalBands
            ? (Average(i => (i, 0)), Average(i => (i, sample - 1)))
            : (Average(i => (0, i)), Average(i => (sample - 1, i)));
    }

    /// <summary>Rectángulo centrado dentro de <paramref name="area"/> que mantiene la proporción (sin recortar).</summary>
    public static SKRect FitRect(int srcW, int srcH, SKRect area)
    {
        var scale = Math.Min(area.Width / srcW, area.Height / srcH);
        var w = srcW * scale;
        var h = srcH * scale;
        return SKRect.Create(area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
    }

    /// <summary>Rectángulo de la fuente que, escalado, cubre exactamente el destino (recorte centrado).</summary>
    public static SKRect CoverSourceRect(int srcW, int srcH, int dstW, int dstH)
    {
        var srcAspect = (double)srcW / srcH;
        var dstAspect = (double)dstW / dstH;
        if (srcAspect > dstAspect)
        {
            var cropW = srcH * dstAspect;
            var left = (srcW - cropW) / 2;
            return SKRect.Create((float)left, 0, (float)cropW, srcH);
        }
        var cropH = srcW / dstAspect;
        var top = (srcH - cropH) / 2;
        return SKRect.Create(0, (float)top, srcW, (float)cropH);
    }

    /// <summary>Escribe la densidad (dpi) en el segmento JFIF APP0 que genera libjpeg, para que laboratorios y drivers tomen 300 dpi.</summary>
    private static void SetJfifDpi(byte[] jpeg, int dpi)
    {
        // FF D8 | FF E0 len(2) 'J' 'F' 'I' 'F' 0 | ver(2) | units(1) | Xdensity(2) | Ydensity(2)
        if (jpeg.Length < 18 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 || jpeg[2] != 0xFF || jpeg[3] != 0xE0) return;
        if (jpeg[6] != (byte)'J' || jpeg[7] != (byte)'F' || jpeg[8] != (byte)'I' || jpeg[9] != (byte)'F' || jpeg[10] != 0) return;
        jpeg[13] = 1; // dots per inch
        jpeg[14] = (byte)(dpi >> 8); jpeg[15] = (byte)dpi;
        jpeg[16] = (byte)(dpi >> 8); jpeg[17] = (byte)dpi;
    }

    private SKImage? GetFrame(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        var writeTime = File.GetLastWriteTimeUtc(path);
        lock (_cacheLock)
        {
            if (_frameCache.TryGetValue(path, out var cached) && cached.WriteTime == writeTime)
                return cached.Image;
            try
            {
                using var encoded = SKData.Create(path);
                var image = SKImage.FromEncodedData(encoded)?.ToRasterImage(ensurePixelData: true);
                if (image == null) return null;
                if (_frameCache.Remove(path, out var old)) old.Image.Dispose();
                _frameCache[path] = (writeTime, image);
                _logger?.Info($"FiguritaComposer: loaded frame {Path.GetFileName(path)} {image.Width}x{image.Height}");
                return image;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"FiguritaComposer: could not load frame {path}: {ex.Message}");
                return null;
            }
        }
    }

    public void Dispose()
    {
        lock (_cacheLock)
        {
            foreach (var entry in _frameCache.Values) entry.Image.Dispose();
            _frameCache.Clear();
        }
    }
}
