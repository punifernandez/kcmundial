using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KCMundial.Core.Interfaces;

namespace KCMundial.App.Services;

public sealed record PrintOutcome(bool Success, string Message);

/// <summary>
/// Imprime en la DNP DP-QW410 (papel 4×6") por el driver de Windows: página 4×6 vertical, sin bordes,
/// la imagen cubre toda la hoja. Los trabajos se encolan de a uno.
/// </summary>
public sealed class PhotoPrinter
{
    private const string DefaultPrinterMatch = "QW410";
    private const double PaperShortInches = 4.0;
    private const double PaperLongInches = 6.0;

    private readonly AppSettings _settings;
    private readonly IAppLogger? _logger;
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    public PhotoPrinter(AppSettings settings, IAppLogger? logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<PrintOutcome> PrintAsync(string imagePath, int copies)
    {
        await _queueLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await RunOnStaThreadAsync(() => PrintCore(imagePath, copies)).ConfigureAwait(false);
        }
        finally
        {
            _queueLock.Release();
        }
    }

    private PrintOutcome PrintCore(string imagePath, int copies)
    {
        if (!File.Exists(imagePath))
            return new PrintOutcome(false, "No se encontró la foto para imprimir.");

        using var server = new LocalPrintServer();
        var queue = FindQueue(server);
        if (queue == null)
        {
            _logger?.Warn("Print: printer not found");
            return new PrintOutcome(false, "No se encontró la impresora DNP.");
        }

        using (queue)
        {
            queue.Refresh();
            var problem = DescribeProblem(queue);

            var ticket = BuildTicket(queue, copies);
            var (pageW, pageH) = PageSize(ticket);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();

            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                // Cubrir la hoja completa (recorte centrado si la proporción no coincide).
                var scale = Math.Max(pageW / bitmap.PixelWidth, pageH / bitmap.PixelHeight);
                var drawW = bitmap.PixelWidth * scale;
                var drawH = bitmap.PixelHeight * scale;
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, pageW, pageH)));
                dc.DrawImage(bitmap, new Rect((pageW - drawW) / 2, (pageH - drawH) / 2, drawW, drawH));
                dc.Pop();
            }

            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(visual, ticket);
            _logger?.Info($"Print: sent {Path.GetFileName(imagePath)} to \"{queue.FullName}\" page {pageW / 96:0.##}x{pageH / 96:0.##}in copies={ticket.CopyCount}{(problem != null ? $" (printer: {problem})" : "")}");

            return problem == null
                ? new PrintOutcome(true, "¡Tu foto se está imprimiendo!")
                : new PrintOutcome(true, $"Foto enviada, pero la impresora avisa: {problem}");
        }
    }

    private PrintQueue? FindQueue(LocalPrintServer server)
    {
        var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections }).ToList();
        var wanted = string.IsNullOrWhiteSpace(_settings.PrinterName) ? DefaultPrinterMatch : _settings.PrinterName.Trim();
        var match = queues.FirstOrDefault(q => string.Equals(q.FullName, wanted, StringComparison.OrdinalIgnoreCase))
                    ?? queues.FirstOrDefault(q => q.FullName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        foreach (var q in queues)
            if (!ReferenceEquals(q, match)) q.Dispose();
        return match;
    }

    private static PrintTicket BuildTicket(PrintQueue queue, int copies)
    {
        var ticket = queue.DefaultPrintTicket.Clone();
        var caps = queue.GetPrintCapabilities(ticket);

        var media = caps.PageMediaSizeCapability
            .Where(m => m.Width.HasValue && m.Height.HasValue)
            .Select(m => (Media: m, Distance: MediaDistance(m.Width!.Value, m.Height!.Value)))
            .Where(x => x.Distance < 0.5 * 96)
            .OrderBy(x => x.Distance)
            .Select(x => x.Media)
            .FirstOrDefault();
        if (media != null) ticket.PageMediaSize = media;

        ticket.PageOrientation = PageOrientation.Portrait;
        if (caps.PageBorderlessCapability.Contains(PageBorderless.Borderless))
            ticket.PageBorderless = PageBorderless.Borderless;
        ticket.CopyCount = Math.Max(1, Math.Min(copies, caps.MaxCopyCount ?? copies));

        return queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
    }

    /// <summary>Distancia (en 1/96") entre un tamaño de papel y 4×6", en cualquier orientación.</summary>
    private static double MediaDistance(double width, double height)
    {
        var shortSide = Math.Min(width, height);
        var longSide = Math.Max(width, height);
        return Math.Abs(shortSide - PaperShortInches * 96) + Math.Abs(longSide - PaperLongInches * 96);
    }

    private static (double Width, double Height) PageSize(PrintTicket ticket)
    {
        var w = ticket.PageMediaSize?.Width ?? PaperShortInches * 96;
        var h = ticket.PageMediaSize?.Height ?? PaperLongInches * 96;
        return (Math.Min(w, h), Math.Max(w, h));
    }

    private static string? DescribeProblem(PrintQueue q)
    {
        if (q.IsOutOfPaper || q.HasPaperProblem) return "sin papel o problema de papel";
        if (q.IsPaperJammed) return "papel atascado";
        if (q.IsDoorOpened) return "tapa abierta";
        if (q.IsOffline || q.IsNotAvailable) return "desconectada";
        if (q.IsInError) return "error";
        if (q.IsPaused) return "en pausa";
        return null;
    }

    private static Task<T> RunOnStaThreadAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "KCMundial print" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
