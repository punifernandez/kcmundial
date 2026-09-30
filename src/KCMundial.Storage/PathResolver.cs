using KCMundial.Core.Interfaces;

namespace KCMundial.Storage;

public sealed class PathResolver : IPathResolver
{
    public string RootInstallPath { get; }
    public string RawFolder => Path.Combine(RootInstallPath, "raw");
    public string RawDebugFolder => Path.Combine(RootInstallPath, "raw_debug");
    public string FiguritasFolder => Path.Combine(RootInstallPath, "figuritas");
    public string FiguritasHdFolder => Path.Combine(RootInstallPath, "figuritas_hd");
    public string AssetsFolder => Path.Combine(RootInstallPath, "assets");
    public string Back300Path => Path.Combine(AssetsFolder, "back_300.png");
    public string Back600Path => Path.Combine(AssetsFolder, "back_600.png");

    /// <summary>Marco seleccionable por el usuario (1, 2 o 3). Devuelve ruta a Fondo_N.png.</summary>
    public string GetFramePath(int frameIndex)
    {
        var n = Math.Clamp(frameIndex, 1, 3);
        return Path.Combine(AssetsFolder, $"Fondo_{n}.png");
    }

    public PathResolver()
    {
        RootInstallPath = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public void EnsureFolders()
    {
        Directory.CreateDirectory(RawFolder);
        Directory.CreateDirectory(RawDebugFolder);
        Directory.CreateDirectory(FiguritasFolder);
        Directory.CreateDirectory(FiguritasHdFolder);
        Directory.CreateDirectory(AssetsFolder);
    }
}
