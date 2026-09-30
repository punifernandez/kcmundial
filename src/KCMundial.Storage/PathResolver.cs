using KCMundial.Core.Interfaces;

namespace KCMundial.Storage;

public sealed class PathResolver : IPathResolver
{
    public string RootInstallPath { get; }
    public string RawFolder => Path.Combine(RootInstallPath, "raw");
    public string FiguritasFolder => Path.Combine(RootInstallPath, "figuritas");
    public string FiguritasHdFolder => Path.Combine(RootInstallPath, "figuritas_hd");
    public string Ampliaciones20x30Folder => Path.Combine(RootInstallPath, "ampliaciones_20x30");
    public string AssetsFolder => Path.Combine(RootInstallPath, "assets");

    /// <summary>Marco seleccionable por el usuario (1, 2 o 3). Devuelve ruta a Fondo_N.png.</summary>
    public string GetFramePath(int frameIndex)
    {
        var n = Math.Clamp(frameIndex, 1, 3);
        return Path.Combine(AssetsFolder, $"Fondo_{n}.png");
    }

    public PathResolver(string? rootInstallPath = null)
    {
        RootInstallPath = (rootInstallPath ?? AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public void EnsureFolders()
    {
        Directory.CreateDirectory(RawFolder);
        Directory.CreateDirectory(FiguritasFolder);
        Directory.CreateDirectory(FiguritasHdFolder);
        Directory.CreateDirectory(Ampliaciones20x30Folder);
        Directory.CreateDirectory(AssetsFolder);
    }
}
