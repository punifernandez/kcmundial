namespace KCMundial.Core.Interfaces;

/// <summary>
/// Resolves install root and output folders.
/// </summary>
public interface IPathResolver
{
    string RootInstallPath { get; }
    /// <summary>Foto original de la cámara (ya rotada), sin marco.</summary>
    string RawFolder { get; }
    /// <summary>Figurita 5×7 cm (1182×1654) + metadata JSON. Es la que se muestra y se comparte.</summary>
    string FiguritasFolder { get; }
    /// <summary>Hoja lista para la impresora (figurita entera centrada, con margen).</summary>
    string ImpresionFolder { get; }
    /// <summary>Máster en alta (foto + marco, lado largo 3600 px).</summary>
    string FiguritasHdFolder { get; }
    /// <summary>Listo para ampliar a 20×30 cm a 300 dpi (2362×3543).</summary>
    string Ampliaciones20x30Folder { get; }
    string AssetsFolder { get; }

    /// <summary>Ruta al marco N (desde 1) del formato: Fondo_N.png (figurita) o Grande_N.png (foto grande).</summary>
    string GetFramePath(Models.PhotoFormat format, int frameIndex);

    /// <summary>Cuántos marcos hay para el formato (archivos consecutivos desde el 1).</summary>
    int GetFrameCount(Models.PhotoFormat format);

    /// <summary>
    /// Ensure all required folders exist.
    /// </summary>
    void EnsureFolders();
}
