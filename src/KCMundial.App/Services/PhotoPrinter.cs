using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.IO;
using System.Printing;
using KCMundial.Core.Interfaces;

namespace KCMundial.App.Services;

public sealed record PrintOutcome(bool Success, string Message);

/// <summary>
/// Imprime en la DNP DP-QW410 por la vía GDI de Windows (System.Drawing.Printing): es la que expone los
/// tamaños de papel del driver de DNP, incluidos los de corte automático. Elige el papel por nombre
/// (<see cref="AppSettings.PrintPaperName"/>) o, si no hay, el más parecido al tamaño configurado
/// (por defecto 3×4": la hoja 4×6 cortada al medio, como la app anterior). La imagen va entera, sin recortar.
/// Los trabajos se encolan de a uno.
/// </summary>
public sealed class PhotoPrinter
{
    private const string DefaultPrinterMatch = "QW410";
    /// <summary>Tolerancia para aceptar un papel "parecido" (en centésimas de pulgada, sumando ambos lados).</summary>
    private const int PaperTolerance = 50;

    private readonly AppSettings _settings;
    private readonly IAppLogger? _logger;
    private readonly SemaphoreSlim _queueLock = new(1, 1);
    private bool _loggedPaperList;

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
            return await Task.Run(() => PrintCore(imagePath, copies)).ConfigureAwait(false);
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
            _logger?.Warn($"Print: printer not found. Installed: {string.Join("; ", PrinterSettings.InstalledPrinters.Cast<string>())}");
            return new PrintOutcome(false, "No se encontró la impresora DNP.");
        }

        using var image = Image.FromFile(imagePath);
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
            return new PrintOutcome(false, $"La impresora \"{printerName}\" no está disponible.");

        var paper = ChoosePaper(doc.PrinterSettings);
        if (paper != null)
            doc.DefaultPageSettings.PaperSize = paper;
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
            // Entera y centrada: nunca se recorta el marco (la imagen ya trae su margen blanco).
            var scale = Math.Min(areaW / image.Width, areaH / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;
            g.DrawImage(image, (areaW - w) / 2, (areaH - h) / 2, w, h);
            e.HasMorePages = false;
        };

        var problem = DescribeProblem(printerName);
        doc.Print();

        var chosen = doc.DefaultPageSettings.PaperSize;
        _logger?.Info($"Print: sent {Path.GetFileName(imagePath)} ({image.Width}x{image.Height}) to \"{printerName}\" " +
                      $"paper=\"{chosen.PaperName}\" {chosen.Width / 100.0:0.##}x{chosen.Height / 100.0:0.##}in " +
                      $"page={drawnPage.Width / 100.0:0.##}x{drawnPage.Height / 100.0:0.##}in rotated={rotated} copies={doc.PrinterSettings.Copies}" +
                      (problem != null ? $" (printer: {problem})" : ""));

        return problem == null
            ? new PrintOutcome(true, "¡Tu foto se está imprimiendo!")
            : new PrintOutcome(true, $"Foto enviada, pero la impresora avisa: {problem}");
    }

    private string? FindPrinter()
    {
        var installed = PrinterSettings.InstalledPrinters.Cast<string>().ToList();
        var wanted = string.IsNullOrWhiteSpace(_settings.PrinterName) ? DefaultPrinterMatch : _settings.PrinterName.Trim();
        return installed.FirstOrDefault(p => string.Equals(p, wanted, StringComparison.OrdinalIgnoreCase))
               ?? installed.FirstOrDefault(p => p.Contains(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Papel por nombre configurado, o el más parecido al tamaño configurado (en cualquier orientación).</summary>
    private PaperSize? ChoosePaper(PrinterSettings printer)
    {
        var papers = printer.PaperSizes.Cast<PaperSize>().ToList();
        if (!_loggedPaperList)
        {
            _loggedPaperList = true;
            _logger?.Info("Print: driver paper sizes: " + string.Join("; ", papers.Select(p =>
                $"\"{p.PaperName}\" {p.Width / 100.0:0.##}x{p.Height / 100.0:0.##}in")));
        }

        if (!string.IsNullOrWhiteSpace(_settings.PrintPaperName))
        {
            var name = _settings.PrintPaperName.Trim();
            var byName = papers.FirstOrDefault(p => string.Equals(p.PaperName, name, StringComparison.OrdinalIgnoreCase))
                         ?? papers.FirstOrDefault(p => p.PaperName.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
            _logger?.Warn($"Print: paper \"{name}\" not found, trying by size");
        }

        var wantShort = (int)Math.Round(Math.Min(_settings.PrintPageWidthInches, _settings.PrintPageHeightInches) * 100);
        var wantLong = (int)Math.Round(Math.Max(_settings.PrintPageWidthInches, _settings.PrintPageHeightInches) * 100);
        var best = papers
            .Select(p => (Paper: p, Distance: Math.Abs(Math.Min(p.Width, p.Height) - wantShort) + Math.Abs(Math.Max(p.Width, p.Height) - wantLong)))
            .Where(x => x.Distance <= PaperTolerance)
            .OrderBy(x => x.Distance)
            .Select(x => x.Paper)
            .FirstOrDefault();
        if (best == null)
            _logger?.Warn($"Print: no paper near {_settings.PrintPageWidthInches}x{_settings.PrintPageHeightInches}in, using printer default " +
                          "(set PrintPaperName in kcmundial.settings.json with one of the driver paper sizes)");
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
            _logger?.Info($"Print: could not read printer status: {ex.Message}");
        }
        return null;
    }
}
