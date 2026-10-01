using System.Diagnostics;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;
using KCMundial.Storage;
using SkiaSharp;

namespace KCMundial.Processing;

/// <summary>
/// Archivos generados para una foto. <see cref="FiguritaPath"/> es la versión para pantalla y QR;
/// <see cref="PrintPath"/> lo que va a la impresora (hoja 3×4" de la DNP, o el máster A4 apaisado para la Epson).
/// </summary>
public sealed record ExportResult(string Id, PhotoFormat Format, string FiguritaPath, string PrintPath, string MasterPath, string? Ampliacion20x30Path, string RawPath);

/// <summary>Hoja que se manda a la impresora (en pulgadas, vertical) y margen blanco de seguridad.</summary>
public sealed record PrintPageSpec(double WidthInches, double HeightInches, double MarginMm);

public sealed class ExportService
{
    private readonly IPathResolver _pathResolver;
    private readonly IFileNaming _fileNaming;
    private readonly FiguritaComposer _composer;
    private readonly MetadataWriter? _metadataWriter;
    private readonly IPhotoUploadService? _uploadService;
    private readonly PrintPageSpec _printPage;
    private readonly IAppLogger? _logger;

    /// <summary>La subida terminó: (id, url pública) o (id, null) si falló.</summary>
    public event Action<string, string?>? UploadFinished;

    public ExportService(
        IPathResolver pathResolver,
        IFileNaming fileNaming,
        FiguritaComposer composer,
        PrintPageSpec printPage,
        MetadataWriter? metadataWriter = null,
        IPhotoUploadService? uploadService = null,
        IAppLogger? logger = null)
    {
        _pathResolver = pathResolver;
        _fileNaming = fileNaming;
        _composer = composer;
        _printPage = printPage;
        _metadataWriter = metadataWriter;
        _uploadService = uploadService;
        _logger = logger;
    }

    /// <summary>
    /// Genera los archivos de una foto. Siempre: raw (original rotado), figuritas_hd (máster con marco) y
    /// figuritas (versión para pantallas y QR). Figurita además: impresion (hoja 3×4" para la DNP) y
    /// ampliaciones_20x30. Foto grande: se imprime el máster (A4 apaisado) en la Epson.
    /// La subida para el QR arranca en segundo plano y avisa con <see cref="UploadFinished"/>.
    /// </summary>
    public async Task<ExportResult?> ExportAsync(CaptureResult capture, PhotoFormat format, int frameIndex, int rotationClockwise, CancellationToken cancellationToken = default)
    {
        var id = _fileNaming.NewId();
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await Task.Run(async () =>
            {
                _pathResolver.EnsureFolders();
                using var photo = FiguritaComposer.CreateUprightPhoto(capture.Bgra, capture.Width, capture.Height, rotationClockwise);
                cancellationToken.ThrowIfCancellationRequested();
                using var master = _composer.ComposeMaster(photo, _pathResolver.GetFramePath(format, frameIndex));
                var composeMs = sw.ElapsedMilliseconds;

                var displayPath = Path.Combine(_pathResolver.FiguritasFolder, id + ".jpg");
                var masterPath = Path.Combine(_pathResolver.FiguritasHdFolder, id + ".jpg");
                var rawPath = Path.Combine(_pathResolver.RawFolder, id + ".jpg");
                var isFigurita = format == PhotoFormat.Figurita;
                var printPath = isFigurita ? Path.Combine(_pathResolver.ImpresionFolder, id + ".jpg") : masterPath;
                var bigPath = isFigurita ? Path.Combine(_pathResolver.Ampliaciones20x30Folder, id + ".jpg") : null;

                // Las imágenes raster de Skia son inmutables: se pueden leer desde varios hilos a la vez.
                var tasks = new List<Task>
                {
                    Task.Run(() => WriteAtomic(displayPath, FiguritaComposer.EncodeJpeg(master, DisplaySize(master), 95)), cancellationToken),
                    Task.Run(() => WriteAtomic(masterPath, FiguritaComposer.EncodeJpeg(master, 95)), cancellationToken),
                    Task.Run(() => WriteAtomic(rawPath, FiguritaComposer.EncodeJpeg(photo, 97)), cancellationToken)
                };
                if (isFigurita)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        using var page = FiguritaComposer.RenderPrintPage(master, _printPage.WidthInches, _printPage.HeightInches, _printPage.MarginMm);
                        WriteAtomic(printPath, FiguritaComposer.EncodeJpeg(page, 97));
                    }, cancellationToken));
                    tasks.Add(Task.Run(() =>
                    {
                        using var big = FiguritaComposer.RenderWithBands(master, OutputSizes.Print20x30);
                        WriteAtomic(bigPath!, FiguritaComposer.EncodeJpeg(big, 97));
                    }, cancellationToken));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);

                _metadataWriter?.Write(new FiguritaMetadata { Id = id, CreatedAt = DateTime.UtcNow, Format = format });
                _logger?.Info($"Export {id} ({format}): capture {capture.Width}x{capture.Height} (highRes={capture.IsHighRes}, rot={rotationClockwise}), " +
                              $"master {master.Width}x{master.Height}, compose {composeMs} ms, total {sw.ElapsedMilliseconds} ms");
                return new ExportResult(id, format, displayPath, printPath, masterPath, bigPath, rawPath);
            }, cancellationToken).ConfigureAwait(false);

            _ = UploadInBackgroundAsync(id, result.FiguritaPath);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error($"Export failed for {id}", ex);
            return null;
        }
    }

    private async Task UploadInBackgroundAsync(string id, string path)
    {
        string? url = null;
        if (_uploadService != null && _metadataWriter != null)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                url = await _uploadService.UploadAsync(bytes, id + ".jpg").ConfigureAwait(false);
                if (!string.IsNullOrEmpty(url))
                {
                    var meta = _metadataWriter.Read(id) ?? new FiguritaMetadata { Id = id, CreatedAt = DateTime.UtcNow };
                    meta.PermanentUrl = url;
                    _metadataWriter.Write(meta);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Upload failed for {id}: {ex.Message}");
                url = null;
            }
        }
        UploadFinished?.Invoke(id, url);
    }

    /// <summary>Versión para pantallas y QR: la figurita clásica (1182×1654) o, si es apaisada, 2000 px de ancho.</summary>
    private static SKSizeI DisplaySize(SKImage master)
    {
        if (master.Height >= master.Width) return OutputSizes.Figurita;
        const int width = 2000;
        return new SKSizeI(width, (int)Math.Round(width * (double)master.Height / master.Width));
    }

    /// <summary>Escribe a un temporal y renombra, para que nunca quede un JPEG a medio escribir (impresora, galería, servidor).</summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
