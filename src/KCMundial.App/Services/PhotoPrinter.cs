using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KCMundial.Core.Interfaces;

namespace KCMundial.App.Services;

public sealed record PrintOutcome(bool Success, string Message);

/// <summary>
/// Imprime en la DNP DP-QW410 por el driver de Windows. Elige el tamaño de papel del driver más parecido al
/// configurado (por defecto 3×4": la hoja 4×6 cortada al medio, como la app anterior) y dibuja la imagen entera,
/// sin recortar. Los trabajos se encolan de a uno.
/// </summary>
public sealed class PhotoPrinter
{
    private const string DefaultPrinterMatch = "QW410";

    private readonly AppSettings _settings;
    private readonly IAppLogger? _logger;
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private bool _loggedMediaList;

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
            // Si el driver define la hoja apaisada (p. ej. "4x3"), se gira la imagen para aprovecharla.
            var rotate = (pageW > pageH) != (bitmap.PixelWidth > bitmap.PixelHeight);
            using (var dc = visual.RenderOpen())
            {
                var (areaW, areaH) = rotate ? (pageH, pageW) : (pageW, pageH);
                var scale = Math.Min(areaW / bitmap.PixelWidth, areaH / bitmap.PixelHeight);
                var drawW = bitmap.PixelWidth * scale;
                var drawH = bitmap.PixelHeight * scale;
                if (rotate)
                {
                    dc.PushTransform(new TranslateTransform(pageW, 0));
                    dc.PushTransform(new RotateTransform(90));
                }
                // Entera y centrada: nunca se recorta el marco.
                dc.DrawImage(bitmap, new Rect((areaW - drawW) / 2, (areaH - drawH) / 2, drawW, drawH));
                if (rotate)
                {
                    dc.Pop();
                    dc.Pop();
                }
            }

            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            writer.Write(visual, ticket);
            _logger?.Info($"Print: sent {Path.GetFileName(imagePath)} ({bitmap.PixelWidth}x{bitmap.PixelHeight}) to \"{queue.FullName}\" " +
                          $"media={ticket.PageMediaSize?.PageMediaSizeName?.ToString() ?? "?"} page {pageW / 96:0.##}x{pageH / 96:0.##}in rotated={rotate} " +
                          $"borderless={ticket.PageBorderless} copies={ticket.CopyCount}{(problem != null ? $" (printer: {problem})" : "")}");

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

    private PrintTicket BuildTicket(PrintQueue queue, int copies)
    {
        var ticket = queue.DefaultPrintTicket.Clone();
        var caps = queue.GetPrintCapabilities(ticket);
        var sizes = caps.PageMediaSizeCapability.Where(m => m.Width.HasValue && m.Height.HasValue).ToList();

        if (!_loggedMediaList)
        {
            _loggedMediaList = true;
            _logger?.Info("Print: driver paper sizes: " + string.Join("; ", sizes.Select(m =>
                $"{m.PageMediaSizeName?.ToString() ?? "(sin nombre)"} {m.Width!.Value / 96:0.##}x{m.Height!.Value / 96:0.##}in")));
        }

        var wantShort = Math.Min(_settings.PrintPageWidthInches, _settings.PrintPageHeightInches) * 96;
        var wantLong = Math.Max(_settings.PrintPageWidthInches, _settings.PrintPageHeightInches) * 96;
        var media = sizes
            .Select(m => (Media: m, Distance: Math.Abs(Math.Min(m.Width!.Value, m.Height!.Value) - wantShort) +
                                              Math.Abs(Math.Max(m.Width!.Value, m.Height!.Value) - wantLong)))
            .Where(x => x.Distance < 0.5 * 96)
            .OrderBy(x => x.Distance)
            .Select(x => x.Media)
            .FirstOrDefault();
        if (media != null)
            ticket.PageMediaSize = media;
        else
            _logger?.Warn($"Print: no paper size near {_settings.PrintPageWidthInches}x{_settings.PrintPageHeightInches}in, using printer default");

        ticket.PageOrientation = PageOrientation.Portrait;
        if (caps.PageBorderlessCapability.Contains(PageBorderless.Borderless))
            ticket.PageBorderless = PageBorderless.Borderless;
        ticket.CopyCount = Math.Max(1, Math.Min(copies, caps.MaxCopyCount ?? copies));

        return queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
    }

    /// <summary>Tamaño de la hoja tal como la define el driver (en 1/96"), considerando la orientación.</summary>
    private (double Width, double Height) PageSize(PrintTicket ticket)
    {
        var w = ticket.PageMediaSize?.Width ?? _settings.PrintPageWidthInches * 96;
        var h = ticket.PageMediaSize?.Height ?? _settings.PrintPageHeightInches * 96;
        var landscape = ticket.PageOrientation is PageOrientation.Landscape or PageOrientation.ReverseLandscape;
        return landscape ? (h, w) : (w, h);
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
