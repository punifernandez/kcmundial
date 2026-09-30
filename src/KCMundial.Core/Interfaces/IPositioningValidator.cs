using KCMundial.Core.Models;

namespace KCMundial.Core.Interfaces;

/// <summary>
/// Validates face position/size against the bust guide (ellipse + size ratios).
/// </summary>
public interface IPositioningValidator
{
    /// <summary>
    /// Frame dimensions (preview) for mapping ellipse.
    /// </summary>
    void SetFrameSize(int width, int height);

    /// <summary>
    /// Validate detected face(s) and return guidance.
    /// </summary>
    PositioningResult Validate(int faceCount, FaceInfo? primaryFace);

    /// <summary>
    /// Min face width as ratio of frame width (default 0.12).
    /// </summary>
    double MinFaceWidthRatio { get; set; }

    /// <summary>
    /// Max face width as ratio of frame width (default 0.22).
    /// </summary>
    double MaxFaceWidthRatio { get; set; }
}
