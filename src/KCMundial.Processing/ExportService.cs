using System.Diagnostics;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;
using KCMundial.Storage;
using SkiaSharp;

namespace KCMundial.Processing;

public sealed class ExportService
{
    private readonly IPathResolver _pathResolver;
    private readonly IFileNaming _fileNaming;
    private readonly IStickerComposer _composer;
    private readonly IFaceDetector _faceDetector;
    private readonly MetadataWriter? _metadataWriter;
    private readonly IPhotoUploadService? _uploadService;
    private readonly IAppLogger? _logger;

    public ExportService(
        IPathResolver pathResolver,
        IFileNaming fileNaming,
        IStickerComposer composer,
        IFaceDetector faceDetector,
        MetadataWriter? metadataWriter = null,
        IPhotoUploadService? uploadService = null,
        IAppLogger? logger = null)
    {
        _pathResolver = pathResolver;
        _fileNaming = fileNaming;
        _composer = composer;
        _faceDetector = faceDetector;
        _metadataWriter = metadataWriter;
        _uploadService = uploadService;
        _logger = logger;
    }

    /// <summary>
    /// Save raw capture, compose figurita a 300 DPI (591×827) y guarda en disco. frameIndex 1–3 selecciona Fondo_1/2/3.png.
    /// </summary>
    public async Task<string?> ExportAsync(CaptureResult capture, int frameIndex = 1, CancellationToken cancellationToken = default)
    {
        var id = _fileNaming.NewId();
        var sw = Stopwatch.StartNew();
        try
        {
            await Task.Run(() =>
            {
                _pathResolver.EnsureFolders();
                var rawPath = Path.Combine(_pathResolver.RawFolder, id + ".jpg");
                SaveBgrAsJpeg(capture.Bgr, capture.Width, capture.Height, rawPath);

                var detectSw = Stopwatch.StartNew();
                var faces = _faceDetector.Detect(capture.Bgr, capture.Width, capture.Height);
                detectSw.Stop();
                _logger?.Info($"Face detection: {faces.Count} face(s), {detectSw.ElapsedMilliseconds} ms");

                IReadOnlyList<float>? scores = null;
                if (_faceDetector is IFaceDetectorWithScores withScores)
                    scores = withScores.GetLastScores();
                var debugLabel = _faceDetector is IFaceDetectorDebugInfo di ? di.GetLastPreprocessInfo() : null;

                var rawDebugPath = Path.Combine(_pathResolver.RawDebugFolder, id + ".jpg");
                SaveBgrWithRectsAndScoresAsJpeg(capture.Bgr, capture.Width, capture.Height, faces, scores, rawDebugPath, debugLabel);

                // Framing: use first face if any, else center of image
                double faceCenterX, faceCenterY, faceHeightPx, eyesY;
                FaceBox? faceBox = null;
                if (faces.Count > 0)
                {
                    var face = faces[0];
                    faceCenterX = face.CenterX;
                    faceCenterY = face.CenterY;
                    faceHeightPx = (double)face.Height;
                    eyesY = face.Y + face.Height * 0.35;
                    faceBox = new FaceBox { X = face.X, Y = face.Y, Width = face.Width, Height = face.Height };
                }
                else
                {
                    faceCenterX = capture.Width / 2.0;
                    faceCenterY = capture.Height / 2.0;
                    faceHeightPx = Math.Max(80, capture.Height * 0.25);
                    eyesY = faceCenterY - faceHeightPx * 0.2;
                }

                // Un solo tamaño 300 DPI (591×827), marco elegido por el usuario (Fondo_1/2/3)
                var backPath = _pathResolver.GetFramePath(frameIndex);
                var jpeg = _composer.Compose(capture.Bgr, capture.Width, capture.Height,
                    faceCenterX, faceCenterY, faceHeightPx, eyesY,
                    591, 827, 95, backPath);

                File.WriteAllBytes(Path.Combine(_pathResolver.FiguritasFolder, id + ".jpg"), jpeg);

                if (_metadataWriter != null)
                    _metadataWriter.Write(new FiguritaMetadata
                    {
                        Id = id,
                        CreatedAt = DateTime.UtcNow,
                        FaceBox = faceBox
                    });
            }, cancellationToken).ConfigureAwait(false);

            // Subir al servidor de hosting y guardar el link permanente para el QR
            if (id != null && _uploadService != null && _metadataWriter != null)
            {
                try
                {
                    var figuritaPath = Path.Combine(_pathResolver.FiguritasFolder, id + ".jpg");
                    if (File.Exists(figuritaPath))
                    {
                        var bytes = await File.ReadAllBytesAsync(figuritaPath, cancellationToken).ConfigureAwait(false);
                        var url = await _uploadService.UploadAsync(bytes, id + ".jpg", cancellationToken).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(url))
                        {
                            var meta = _metadataWriter.Read(id) ?? new FiguritaMetadata { Id = id };
                            meta.PermanentUrl = url;
                            _metadataWriter.Write(meta);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"Upload after export failed for {id}: {ex.Message}");
                }
            }

            sw.Stop();
            _logger?.Info($"Export {id} completed in {sw.ElapsedMilliseconds} ms");
            return id;
        }
        catch (Exception ex)
        {
            _logger?.Error($"Export failed for {id}", ex);
            return null;
        }
    }

    /// <summary>
    /// Run face detection on the given frame and save to raw_debug with bbox, scores, and preprocess label (for periodic debug dumps).
    /// </summary>
    public void SaveDebugFrame(byte[] bgr, int width, int height)
    {
        try
        {
            _pathResolver.EnsureFolders();
            var faces = _faceDetector.Detect(bgr, width, height);
            IReadOnlyList<float>? scores = null;
            if (_faceDetector is IFaceDetectorWithScores withScores)
                scores = withScores.GetLastScores();
            var debugLabel = _faceDetector is IFaceDetectorDebugInfo di ? di.GetLastPreprocessInfo() : null;
            var name = "debug_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".jpg";
            var path = Path.Combine(_pathResolver.RawDebugFolder, name);
            SaveBgrWithRectsAndScoresAsJpeg(bgr, width, height, faces, scores, path, debugLabel);
        }
        catch (Exception ex)
        {
            _logger?.Warn($"SaveDebugFrame failed: {ex.Message}");
        }
    }

    private static void SaveBgrAsJpeg(byte[] bgr, int width, int height, string path)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = bitmap.Pixels;
        for (var i = 0; i < width * height; i++)
        {
            pixels[i] = new SKColor(bgr[i * 3 + 2], bgr[i * 3 + 1], bgr[i * 3], 255);
        }
        bitmap.Pixels = pixels;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    private static void SaveBgrWithRectsAndScoresAsJpeg(
        byte[] bgr, int width, int height,
        IReadOnlyList<FaceRect> rects,
        IReadOnlyList<float>? scores,
        string path,
        string? preprocessLabel = null)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = bitmap.Pixels;
        for (var i = 0; i < width * height; i++)
            pixels[i] = new SKColor(bgr[i * 3 + 2], bgr[i * 3 + 1], bgr[i * 3], 255);
        bitmap.Pixels = pixels;

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.DrawBitmap(bitmap, 0, 0);

        using var strokePaint = new SKPaint { Color = new SKColor(0, 255, 0), Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        using var textPaint = new SKPaint { Color = new SKColor(255, 255, 0), TextSize = Math.Max(14, width / 40), IsAntialias = true };
        for (var i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            canvas.DrawRect(SKRect.Create(r.X, r.Y, r.Width, r.Height), strokePaint);
            var scoreText = (scores != null && i < scores.Count) ? $"{scores[i]:F2}" : "";
            if (scoreText.Length > 0)
                canvas.DrawText(scoreText, r.X, Math.Max(r.Y + 16, 16), textPaint);
        }

        if (!string.IsNullOrEmpty(preprocessLabel))
        {
            using var labelPaint = new SKPaint { Color = new SKColor(200, 200, 200), TextSize = Math.Max(12, width / 50), IsAntialias = true };
            canvas.DrawText(preprocessLabel, 8, height - 8, labelPaint);
        }

        using var outImage = surface.Snapshot();
        using var data = outImage.Encode(SKEncodedImageFormat.Jpeg, 92);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }
}
