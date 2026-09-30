namespace KCMundial.Core.Interfaces;

/// <summary>
/// Resolves install root and folder paths for raw, figuritas, figuritas_hd, assets.
/// </summary>
public interface IPathResolver
{
    string RootInstallPath { get; }
    string RawFolder { get; }
    string RawDebugFolder { get; }
    string FiguritasFolder { get; }
    string FiguritasHdFolder { get; }
    string AssetsFolder { get; }
    string Back300Path { get; }
    string Back600Path { get; }

    /// <summary>Ruta al marco seleccionable (1, 2 o 3). Ej: Fondo_1.png, Fondo_2.png, Fondo_3.png.</summary>
    string GetFramePath(int frameIndex);

    /// <summary>
    /// Ensure all required folders exist.
    /// </summary>
    void EnsureFolders();
}
