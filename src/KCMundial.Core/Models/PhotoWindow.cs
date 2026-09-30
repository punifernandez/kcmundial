namespace KCMundial.Core.Models;

/// <summary>
/// Photo window region on the background template (normalized 0..1).
/// </summary>
public sealed class PhotoWindow
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }

    public static PhotoWindow Default => new()
    {
        X = 0.12,
        Y = 0.17,
        Width = 0.76,
        Height = 0.63
    };
}
