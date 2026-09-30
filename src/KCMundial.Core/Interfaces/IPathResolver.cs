namespace KCMundial.Core.Interfaces;

/// <summary>
/// Resolves install root and output folders.
/// </summary>
public interface IPathResolver
{
    string RootInstallPath { get; }
    /// <summary>Foto original de la cámara (ya rotada), sin marco.</summary>
    string RawFolder { get; }
    /// <summary>Archivo de impresión 4×6" (1200×1800) + metadata JSON. Es el que se muestra y se comparte.</summary>
    string FiguritasFolder { get; }
    /// <summary>Máster en alta (foto + marco, lado largo 3600 px).</summary>
    string FiguritasHdFolder { get; }
    /// <summary>Listo para ampliar a 20×30 cm a 300 dpi (2362×3543).</summary>
    string Ampliaciones20x30Folder { get; }
    string AssetsFolder { get; }

    /// <summary>Ruta al marco seleccionable (1, 2 o 3). Ej: Fondo_1.png, Fondo_2.png, Fondo_3.png.</summary>
    string GetFramePath(int frameIndex);

    /// <summary>
    /// Ensure all required folders exist.
    /// </summary>
    void EnsureFolders();
}
