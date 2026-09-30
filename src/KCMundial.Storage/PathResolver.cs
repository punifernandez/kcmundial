using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;

namespace KCMundial.Storage;

public sealed class PathResolver : IPathResolver
{
    public string RootInstallPath { get; }
    public string RawFolder => Path.Combine(RootInstallPath, "raw");
    public string FiguritasFolder => Path.Combine(RootInstallPath, "figuritas");
    public string ImpresionFolder => Path.Combine(RootInstallPath, "impresion");
    public string FiguritasHdFolder => Path.Combine(RootInstallPath, "figuritas_hd");
    public string Ampliaciones20x30Folder => Path.Combine(RootInstallPath, "ampliaciones_20x30");
    public string AssetsFolder => Path.Combine(RootInstallPath, "assets");

    private const int MaxFrames = 6;

    /// <summary>Marco N del formato: Fondo_N.png (figurita, vertical) o Grande_N.png (foto grande, apaisada).</summary>
    public string GetFramePath(PhotoFormat format, int frameIndex)
    {
        var n = Math.Clamp(frameIndex, 1, MaxFrames);
        var prefix = format == PhotoFormat.Grande ? "Grande" : "Fondo";
        return Path.Combine(AssetsFolder, $"{prefix}_{n}.png");
    }

    public int GetFrameCount(PhotoFormat format)
    {
        var count = 0;
        while (count < MaxFrames && File.Exists(GetFramePath(format, count + 1)))
            count++;
        return count;
    }

    public PathResolver(string? rootInstallPath = null)
    {
        RootInstallPath = (rootInstallPath ?? AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public void EnsureFolders()
    {
        Directory.CreateDirectory(RawFolder);
        Directory.CreateDirectory(FiguritasFolder);
        Directory.CreateDirectory(ImpresionFolder);
        Directory.CreateDirectory(FiguritasHdFolder);
        Directory.CreateDirectory(Ampliaciones20x30Folder);
        Directory.CreateDirectory(AssetsFolder);
    }
}
