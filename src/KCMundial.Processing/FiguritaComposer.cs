using KCMundial.Core.Interfaces;
using SkiaSharp;

namespace KCMundial.Processing;

/// <summary>Tamaños de salida (a 300 dpi).</summary>
public static class OutputSizes
{
    /// <summary>Lado largo del máster (foto + marco). Con marcos 2:3 queda en 2400×3600.</summary>
    public const int MasterLongSide = 3600;
    /// <summary>DNP DP-QW410, papel 4×6" a 300 dpi.</summary>
    public static readonly SKSizeI Print4x6 = new(1200, 1800);
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

    /// <summary>Proporción usada cuando no hay marco (2:3 vertical, igual que el papel 4×6").</summary>
    public const double DefaultAspect = 2.0 / 3.0;

    public FiguritaComposer(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>Crea la foto "derecha" a partir del cuadro BGRA de la cámara, aplicando la rotación de montaje (0/90/180/270 horario).</summary>
    public static SKImage CreateUprightPhoto(byte[] bgra, int width, int height, int rotationClockwise)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var source = SKImage.FromPixelCopy(info, bgra, width * 4)
            ?? throw new InvalidOperationException("No se pudo crear la imagen de la cámara.");

        var rotation = NormalizeRotation(rotationClockwise);
        if (rotation == 0)
            return source;

        var swap = rotation is 90 or 270;
        var outW = swap ? height : width;
        var outH = swap ? width : height;
        using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Opaque))
            ?? throw new InvalidOperationException("No se pudo crear la superficie para rotar.");
        var canvas = surface.Canvas;
        canvas.Translate(outW / 2f, outH / 2f);
        canvas.RotateDegrees(rotation);
        canvas.Translate(-width / 2f, -height / 2f);
        canvas.DrawImage(source, 0, 0);
        canvas.Flush();
        source.Dispose();
        return surface.Snapshot();
    }

    public static int NormalizeRotation(int degrees) => ((degrees % 360) + 360) % 360 / 90 * 90;

    /// <summary>Proporción (ancho/alto) del marco, o 2:3 si no existe.</summary>
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
