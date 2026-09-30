using System.Diagnostics;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;
using KCMundial.Storage;
using SkiaSharp;

namespace KCMundial.Processing;

/// <summary>Archivos generados para una foto.</summary>
public sealed record ExportResult(string Id, string PrintPath, string MasterPath, string Ampliacion20x30Path, string RawPath);

public sealed class ExportService
{
    private readonly IPathResolver _pathResolver;
    private readonly IFileNaming _fileNaming;
    private readonly FiguritaComposer _composer;
    private readonly MetadataWriter? _metadataWriter;
    private readonly IPhotoUploadService? _uploadService;
    private readonly IAppLogger? _logger;

    /// <summary>La subida terminó: (id, url pública) o (id, null) si falló.</summary>
    public event Action<string, string?>? UploadFinished;

    public ExportService(
        IPathResolver pathResolver,
        IFileNaming fileNaming,
        FiguritaComposer composer,
        MetadataWriter? metadataWriter = null,
        IPhotoUploadService? uploadService = null,
        IAppLogger? logger = null)
    {
        _pathResolver = pathResolver;
        _fileNaming = fileNaming;
        _composer = composer;
        _metadataWriter = metadataWriter;
        _uploadService = uploadService;
        _logger = logger;
    }

    /// <summary>
    /// Genera todos los archivos de una foto:
    /// raw (original rotado), figuritas_hd (máster), figuritas (4×6 para imprimir) y ampliaciones_20x30.
    /// La subida para el QR arranca en segundo plano y avisa con <see cref="UploadFinished"/>.
    /// </summary>
    public async Task<ExportResult?> ExportAsync(CaptureResult capture, int frameIndex, int rotationClockwise, CancellationToken cancellationToken = default)
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
                using var master = _composer.ComposeMaster(photo, _pathResolver.GetFramePath(frameIndex));
                var composeMs = sw.ElapsedMilliseconds;

                var printPath = Path.Combine(_pathResolver.FiguritasFolder, id + ".jpg");
                var masterPath = Path.Combine(_pathResolver.FiguritasHdFolder, id + ".jpg");
                var bigPath = Path.Combine(_pathResolver.Ampliaciones20x30Folder, id + ".jpg");
                var rawPath = Path.Combine(_pathResolver.RawFolder, id + ".jpg");

                // Las imágenes raster de Skia son inmutables: se pueden leer desde varios hilos a la vez.
                await Task.WhenAll(
                    Task.Run(() => WriteAtomic(printPath, FiguritaComposer.EncodeJpeg(master, OutputSizes.Print4x6, 95)), cancellationToken),
                    Task.Run(() => WriteAtomic(masterPath, FiguritaComposer.EncodeJpeg(master, 95)), cancellationToken),
                    Task.Run(() => WriteAtomic(bigPath, FiguritaComposer.EncodeJpeg(master, OutputSizes.Print20x30, 97)), cancellationToken),
                    Task.Run(() => WriteAtomic(rawPath, FiguritaComposer.EncodeJpeg(photo, 97)), cancellationToken)
                ).ConfigureAwait(false);

                _metadataWriter?.Write(new FiguritaMetadata { Id = id, CreatedAt = DateTime.UtcNow });
                _logger?.Info($"Export {id}: capture {capture.Width}x{capture.Height} (highRes={capture.IsHighRes}, rot={rotationClockwise}), " +
                              $"master {master.Width}x{master.Height}, compose {composeMs} ms, total {sw.ElapsedMilliseconds} ms");
                return new ExportResult(id, printPath, masterPath, bigPath, rawPath);
            }, cancellationToken).ConfigureAwait(false);

            _ = UploadInBackgroundAsync(id, result.PrintPath);
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

    /// <summary>Escribe a un temporal y renombra, para que nunca quede un JPEG a medio escribir (impresora, galería, servidor).</summary>
    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
