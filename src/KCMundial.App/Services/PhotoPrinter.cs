using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using System.Printing;
using KCMundial.Core.Interfaces;

namespace KCMundial.App.Services;

public sealed record PrintOutcome(bool Success, string Message);

/// <summary>
/// Imprime por la vía GDI de Windows (System.Drawing.Printing), que expone los tamaños de papel de los drivers
/// (DNP con corte automático, Epson sin márgenes, etc.). Una instancia por impresora (<see cref="PrintProfile"/>):
/// elige el papel por nombre o, si no hay, el más parecido al tamaño configurado. La imagen va entera, sin
/// recortar, dentro del margen del perfil. Los trabajos se encolan de a uno.
/// </summary>
public sealed class PhotoPrinter
{
    /// <summary>Tolerancia para aceptar un papel "parecido" (en centésimas de pulgada, sumando ambos lados).</summary>
    private const int PaperTolerance = 60;

    private readonly PrintProfile _profile;
    private readonly string _devModePath;
    private readonly IAppLogger? _logger;
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private bool _loggedPaperList;

    /// <param name="devModePath">Archivo donde se guarda la configuración del driver elegida desde la app.</param>
    public PhotoPrinter(PrintProfile profile, string devModePath, IAppLogger? logger)
    {
        _profile = profile;
        _devModePath = devModePath;
        _logger = logger;
    }

    public string Label => _profile.Label;

    /// <summary>Hay una configuración del driver guardada desde la app.</summary>
    public bool HasSavedSetup => File.Exists(_devModePath);

    /// <summary>
    /// Abre la ventana de configuración del fabricante para esta impresora y guarda lo elegido.
    /// Se llama desde el hilo de la UI (la ventana es modal a <paramref name="owner"/>).
    /// </summary>
    public PrintOutcome Configure(IntPtr owner)
    {
        var printerName = FindPrinter();
        if (printerName == null)
            return new PrintOutcome(false, $"No se encontró la impresora {Label} (\"{_profile.PrinterMatch}\").");
        try
        {
            var saved = PrinterSetup.ShowDialogAndSave(owner, printerName, _devModePath);
            _logger?.Info($"Print[{Label}]: setup dialog for \"{printerName}\" {(saved ? "saved" : "cancelled")}");
            return saved
                ? new PrintOutcome(true, $"Configuración de {printerName} guardada.")
                : new PrintOutcome(false, "No se cambió nada.");
        }
        catch (Exception ex)
        {
            _logger?.Error($"Print[{Label}]: setup dialog failed", ex);
            return new PrintOutcome(false, $"No se pudo abrir la configuración: {ex.Message}");
        }
    }

    public async Task<PrintOutcome> PrintAsync(string imagePath)
    {
        await _queueLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => PrintCore(imagePath, _profile.Copies)).ConfigureAwait(false);
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

        var printerName = FindPrinter();
        if (printerName == null)
        {
            _logger?.Warn($"Print[{Label}]: printer \"{_profile.PrinterMatch}\" not found. Installed: {string.Join("; ", PrinterSettings.InstalledPrinters.Cast<string>())}");
            return new PrintOutcome(false, $"No se encontró la impresora {Label}.");
        }

        using var image = Image.FromFile(imagePath);
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
            return new PrintOutcome(false, $"La impresora \"{printerName}\" no está disponible.");

        // Configuración del driver guardada desde la app (papel, tipo de papel, calidad…): tiene prioridad.
        var saved = PrinterSetup.Load(_devModePath);
        if (saved != null)
        {
            PrinterSetup.Apply(doc.PrinterSettings, doc.DefaultPageSettings, saved);
            LogPaperList(doc.PrinterSettings);
        }
        else
        {
            var paper = ChoosePaper(doc.PrinterSettings);
            if (paper != null)
                doc.DefaultPageSettings.PaperSize = paper;
        }
        doc.DefaultPageSettings.Landscape = false;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.OriginAtMargins = false;
        doc.PrinterSettings.Copies = (short)Math.Clamp(copies, 1, Math.Max(1, (int)doc.PrinterSettings.MaximumCopies));
        doc.DocumentName = "KCMundial " + Path.GetFileNameWithoutExtension(imagePath);
        doc.PrintController = new StandardPrintController(); // sin ventana de "Imprimiendo…"

        var rotated = false;
        RectangleF drawnPage = default;
        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;

            // Coordenadas de la hoja física (centésimas de pulgada), corrigiendo el margen no imprimible.
            var page = e.PageBounds;
            g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
            drawnPage = page;

            // Si el driver define la hoja apaisada, se gira la imagen para aprovecharla.
            rotated = (page.Width > page.Height) != (image.Width > image.Height);
            float areaW = rotated ? page.Height : page.Width;
            float areaH = rotated ? page.Width : page.Height;
            if (rotated)
            {
                g.TranslateTransform(page.Width, 0);
                g.RotateTransform(90);
            }
            // Entera y centrada dentro del margen: nunca se recorta el marco.
            var margin = (float)(_profile.MarginMm / 25.4 * 100);
            var fitW = areaW - 2 * margin;
            var fitH = areaH - 2 * margin;
            var scale = Math.Min(fitW / image.Width, fitH / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;
            g.DrawImage(image, (areaW - w) / 2, (areaH - h) / 2, w, h);
            e.HasMorePages = false;
        };

        var problem = DescribeProblem(printerName);
        doc.Print();

        var chosen = doc.DefaultPageSettings.PaperSize;
        _logger?.Info($"Print[{Label}]: sent {Path.GetFileName(imagePath)} ({image.Width}x{image.Height}) to \"{printerName}\" " +
                      $"paper=\"{chosen.PaperName}\" {chosen.Width / 100.0:0.##}x{chosen.Height / 100.0:0.##}in " +
                      $"page={drawnPage.Width / 100.0:0.##}x{drawnPage.Height / 100.0:0.##}in rotated={rotated} copies={doc.PrinterSettings.Copies} " +
                      $"driverSettings={(saved != null ? "saved-in-app" : "windows-defaults")}" +
                      (problem != null ? $" (printer: {problem})" : ""));

        return problem == null
            ? new PrintOutcome(true, "¡Tu foto se está imprimiendo!")
            : new PrintOutcome(true, $"Foto enviada, pero la impresora avisa: {problem}");
    }

    private string? FindPrinter()
    {
        var installed = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        var wanted = _profile.PrinterMatch.Trim();
        return installed.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
               ?? installed.FirstOrDefault(p => p.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    private void LogPaperList(PrinterSettings printer)
    {
        if (_loggedPaperList) return;
        _loggedPaperList = true;
        _logger?.Info($"Print[{Label}]: driver paper sizes: " + string.Join("; ", printer.PaperSizes.Cast<PaperSize>().Select(p =>
            $"\"{p.PaperName}\" {p.Width / 100.0:0.##}x{p.Height / 100.0:0.##}in")));
    }

    /// <summary>Papel por nombre configurado, o el más parecido al tamaño configurado (en cualquier orientación).</summary>
    private PaperSize? ChoosePaper(PrinterSettings printer)
    {
        var papers = printer.PaperSizes.Cast<PaperSize>().ToList();
        LogPaperList(printer);

        if (!string.IsNullOrWhiteSpace(_profile.PaperName))
        {
            var name = _profile.PaperName.Trim();
            var byName = papers.FirstOrDefault(p => string.Equals(p.PaperName, name, StringComparison.OrdinalIgnoreCase))
                         ?? papers.FirstOrDefault(p => p.PaperName.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
            _logger?.Warn($"Print[{Label}]: paper \"{name}\" not found, trying by size");
        }

        var wantShort = (int)Math.Round(Math.Min(_profile.PageWidthInches, _profile.PageHeightInches) * 100);
        var wantLong = (int)Math.Round(Math.Max(_profile.PageWidthInches, _profile.PageHeightInches) * 100);
        var best = papers
            .Select(p => (Paper: p, Distance: Math.Abs(Math.Min(p.Width, p.Height) - wantShort) + Math.Abs(Math.Max(p.Width, p.Height) - wantLong)))
            .Where(x => x.Distance <= PaperTolerance)
            .OrderBy(x => x.Distance)
            .Select(x => x.Paper)
            .FirstOrDefault();
        if (best == null)
            _logger?.Warn($"Print[{Label}]: no paper near {_profile.PageWidthInches}x{_profile.PageHeightInches}in, using printer default " +
                          "(set the paper name in kcmundial.settings.json with one of the driver paper sizes)");
        return best;
    }

    /// <summary>Estado de la cola (papel, tapa, desconectada…). Solo informativo: nunca bloquea la impresión.</summary>
    private string? DescribeProblem(string printerName)
    {
        try
        {
            using var server = new LocalPrintServer();
            using var q = server.GetPrintQueue(printerName);
            q.Refresh();
            if (q.IsOutOfPaper || q.HasPaperProblem) return "sin papel o problema de papel";
            if (q.IsPaperJammed) return "papel atascado";
            if (q.IsDoorOpened) return "tapa abierta";
            if (q.IsOffline || q.IsNotAvailable) return "desconectada";
            if (q.IsInError) return "error";
            if (q.IsPaused) return "en pausa";
        }
        catch (Exception ex)
        {
            _logger?.Info($"Print[{Label}]: could not read printer status: {ex.Message}");
        }
        return null;
    }
}
