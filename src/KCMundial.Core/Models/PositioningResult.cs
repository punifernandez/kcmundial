namespace KCMundial.Core.Models;

/// <summary>
/// Result of face-based positioning validation for the bust guide.
/// </summary>
public sealed class PositioningResult
{
    public bool IsOk { get; init; }
    public string GuidanceMessage { get; init; } = string.Empty;
    public int FaceCount { get; init; }
    public FaceInfo? PrimaryFace { get; init; }
}

/// <summary>
/// Detected face info in frame coordinates.
/// </summary>
public sealed class FaceInfo
{
    public double CenterX { get; init; }
    public double CenterY { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    /// <summary>
    /// Approximate eye line Y (for composition). If not available, use center Y.
    /// </summary>
    public double EyesY { get; init; }
}
