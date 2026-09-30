using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using KCMundial.Core.Interfaces;

namespace KCMundial.App.Services;

/// <summary>
/// Configuración del operador. Se lee de kcmundial.settings.json junto al .exe (si no existe, se crea con estos valores).
/// </summary>
public sealed class AppSettings
{
    public const string FileName = "kcmundial.settings.json";

    /// <summary>Rotación horaria para enderezar la imagen de la cámara: 0, 90, 180 o 270.
    /// Brio apaisada (normal): 0. Montada vertical: 90 o 270.</summary>
    public int CameraRotation { get; set; } = 0;
    /// <summary>Preview en espejo (la foto final nunca sale espejada).</summary>
    public bool MirrorPreview { get; set; } = true;
    /// <summary>Sacar la foto en la resolución máxima de la cámara (si falla, usa el cuadro del preview).</summary>
    public bool HighResCapture { get; set; } = true;
    public int CountdownSeconds { get; set; } = 3;
    /// <summary>Imprimir apenas se saca la foto.</summary>
    public bool AutoPrint { get; set; } = true;
    /// <summary>Nombre (o parte del nombre) de la impresora. Vacío = la primera que contenga "QW410".</summary>
    public string PrinterName { get; set; } = "";
    public int PrintCopies { get; set; } = 1;
    /// <summary>Nombre (o parte) del papel del driver a usar, tal como aparece en el log ("driver paper sizes").
    /// Vacío = el más parecido a PrintPageWidthInches × PrintPageHeightInches.</summary>
    public string PrintPaperName { get; set; } = "";
    /// <summary>Tamaño de la hoja en la DNP (pulgadas). 3×4 = hoja 4×6 cortada al medio por la impresora.</summary>
    public double PrintPageWidthInches { get; set; } = 3.0;
    public double PrintPageHeightInches { get; set; } = 4.0;
    /// <summary>Margen blanco alrededor de la figurita en la hoja (lo que recorta la impresión sin bordes).</summary>
    public double PrintMarginMm { get; set; } = 2.0;
    /// <summary>Impresora XL (botón "Imprimir XL"): nombre o parte del nombre.</summary>
    public string XlPrinterName { get; set; } = "L8050";
    /// <summary>Papel de la impresora XL por nombre, tal como aparece en el log. Vacío = el más parecido al tamaño.</summary>
    public string XlPaperName { get; set; } = "";
    /// <summary>Tamaño de la hoja XL (pulgadas). A4 = 8,27×11,69".</summary>
    public double XlPageWidthInches { get; set; } = 8.27;
    public double XlPageHeightInches { get; set; } = 11.69;
    /// <summary>Margen de seguridad en la hoja XL (la impresión sin bordes recorta un poco los costados).</summary>
    public double XlMarginMm { get; set; } = 3.0;
    public int XlCopies { get; set; } = 1;
    /// <summary>Subir la foto al servidor para el QR.</summary>
    public bool UploadEnabled { get; set; } = true;

    /// <summary>Figurita chica en la DNP (la hoja ya trae su margen blanco).</summary>
    public PrintProfile SmallPrintProfile => new("DNP", string.IsNullOrWhiteSpace(PrinterName) ? "QW410" : PrinterName,
        PrintPaperName, PrintPageWidthInches, PrintPageHeightInches, MarginMm: 0, Copies: PrintCopies);

    /// <summary>Ampliación en la impresora XL.</summary>
    public PrintProfile XlPrintProfile => new("XL", string.IsNullOrWhiteSpace(XlPrinterName) ? "L8050" : XlPrinterName,
        XlPaperName, XlPageWidthInches, XlPageHeightInches, XlMarginMm, XlCopies);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppSettings Load(string folder, IAppLogger? logger)
    {
        var path = Path.Combine(folder, FileName);
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            logger?.Warn($"Settings: could not read {path} ({ex.Message}), using defaults");
            return new AppSettings();
        }

        settings.CountdownSeconds = Math.Clamp(settings.CountdownSeconds, 1, 10);
        settings.PrintCopies = Math.Clamp(settings.PrintCopies, 1, 5);
        settings.PrintPageWidthInches = Math.Clamp(settings.PrintPageWidthInches, 1, 12);
        settings.PrintPageHeightInches = Math.Clamp(settings.PrintPageHeightInches, 1, 12);
        settings.PrintMarginMm = Math.Clamp(settings.PrintMarginMm, 0, 15);
        settings.XlPageWidthInches = Math.Clamp(settings.XlPageWidthInches, 1, 20);
        settings.XlPageHeightInches = Math.Clamp(settings.XlPageHeightInches, 1, 20);
        settings.XlMarginMm = Math.Clamp(settings.XlMarginMm, 0, 25);
        settings.XlCopies = Math.Clamp(settings.XlCopies, 1, 5);
        try
        {
            // Reescribir para que aparezcan las opciones nuevas.
            File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            logger?.Warn($"Settings: could not write {path}: {ex.Message}");
        }
        logger?.Info($"Settings: {JsonSerializer.Serialize(settings)}");
        return settings;
    }
}

/// <summary>A qué impresora, en qué papel y con qué margen se imprime.</summary>
public sealed record PrintProfile(string Label, string PrinterMatch, string PaperName, double PageWidthInches,
    double PageHeightInches, double MarginMm, int Copies);
