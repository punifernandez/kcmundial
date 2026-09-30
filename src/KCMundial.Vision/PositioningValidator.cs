using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;

namespace KCMundial.Vision;

/// <summary>
/// Validates face position/size against a centered ellipse and size ratios.
/// </summary>
public sealed class PositioningValidator : IPositioningValidator
{
    private int _frameWidth = 1;
    private int _frameHeight = 1;
    private double _ellipseCenterX => _frameWidth / 2.0;
    private double _ellipseCenterY => _frameHeight / 2.0;
    private double _ellipseRadiusX => _frameWidth * 0.35;
    private double _ellipseRadiusY => _frameHeight * 0.40;

    // Relativo al ancho del área visible (proporción del marco, 2:3). Equivale a 0.12–0.22 del recorte 9:16 anterior.
    public double MinFaceWidthRatio { get; set; } = 0.10;
    public double MaxFaceWidthRatio { get; set; } = 0.185;

    public void SetFrameSize(int width, int height)
    {
        _frameWidth = Math.Max(1, width);
        _frameHeight = Math.Max(1, height);
    }

    public PositioningResult Validate(int faceCount, FaceInfo? primaryFace)
    {
        // No longer block capture: allow 0 or multiple faces; only give hints
        if (faceCount == 0)
            return new PositioningResult { IsOk = true, GuidanceMessage = "", FaceCount = 0 };

        if (faceCount > 1)
            return new PositioningResult { IsOk = true, GuidanceMessage = "", FaceCount = faceCount };

        if (primaryFace == null)
            return new PositioningResult { IsOk = true, GuidanceMessage = "", FaceCount = 1 };

        var ratio = primaryFace.Width / _frameWidth;
        if (ratio < MinFaceWidthRatio)
            return new PositioningResult { IsOk = true, GuidanceMessage = "Acercate un poco", FaceCount = 1, PrimaryFace = primaryFace };
        if (ratio > MaxFaceWidthRatio)
            return new PositioningResult { IsOk = true, GuidanceMessage = "Alejate un poco", FaceCount = 1, PrimaryFace = primaryFace };

        var dx = (primaryFace.CenterX - _ellipseCenterX) / _ellipseRadiusX;
        var dy = (primaryFace.CenterY - _ellipseCenterY) / _ellipseRadiusY;
        if (dx * dx + dy * dy > 1.0)
            return new PositioningResult { IsOk = true, GuidanceMessage = "Centrate en el marco", FaceCount = 1, PrimaryFace = primaryFace };

        return new PositioningResult { IsOk = true, GuidanceMessage = string.Empty, FaceCount = 1, PrimaryFace = primaryFace };
    }
}
