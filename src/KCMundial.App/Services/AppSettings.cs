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

    /// <summary>Rotación horaria para enderezar la imagen de la cámara: 0, 90, 180 o 270. Con la Brio montada vertical: 90 o 270.</summary>
    public int CameraRotation { get; set; } = 90;
    /// <summary>Preview en espejo (la foto final nunca sale espejada).</summary>
    public bool MirrorPreview { get; set; } = true;
    /// <summary>Sacar la foto en la resolución máxima de la cámara (si falla, usa el cuadro del preview).</summary>
    public bool HighResCapture { get; set; } = true;
    public int CountdownSeconds { get; set; } = 3;
    /// <summary>Segundos en la pantalla de resultado antes de volver solo al inicio.</summary>
    public int ResultAutoReturnSeconds { get; set; } = 25;
    /// <summary>Imprimir apenas se saca la foto.</summary>
    public bool AutoPrint { get; set; } = true;
    /// <summary>Nombre (o parte del nombre) de la impresora. Vacío = la primera que contenga "QW410".</summary>
    public string PrinterName { get; set; } = "";
    public int PrintCopies { get; set; } = 1;
    /// <summary>Subir la foto al servidor para el QR.</summary>
    public bool UploadEnabled { get; set; } = true;

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
        settings.ResultAutoReturnSeconds = Math.Clamp(settings.ResultAutoReturnSeconds, 5, 300);
        settings.PrintCopies = Math.Clamp(settings.PrintCopies, 1, 5);
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
