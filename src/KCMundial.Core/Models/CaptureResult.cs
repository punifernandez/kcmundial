namespace KCMundial.Core.Models;

/// <summary>
/// Result of a high-res still capture: BGR pixel data and dimensions for saving and composition.
/// </summary>
public sealed class CaptureResult
{
    public byte[] Bgr { get; init; } = Array.Empty<byte>();
    public int Width { get; init; }
    public int Height { get; init; }
}
