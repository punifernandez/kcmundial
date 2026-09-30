namespace KCMundial.Core.Models;

/// <summary>
/// Still capture as delivered by the camera (not rotated): BGRA 32bpp, stride = Width * 4.
/// </summary>
public sealed class CaptureResult
{
    public byte[] Bgra { get; init; } = Array.Empty<byte>();
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>True when the still came from the full-resolution capture path (not a preview frame).</summary>
    public bool IsHighRes { get; init; }
}
