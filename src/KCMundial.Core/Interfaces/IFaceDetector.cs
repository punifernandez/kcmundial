namespace KCMundial.Core.Interfaces;

/// <summary>
/// Face detection on a raw image. Returns rectangles in image coordinates.
/// Implementations may use ONNX or OpenCV DNN/Cascade.
/// </summary>
public interface IFaceDetector : IDisposable
{
    /// <summary>
    /// Detect faces in the given BGR image (width x height, step = width * 3).
    /// </summary>
    /// <returns>List of face rectangles (x, y, width, height) in pixel coordinates.</returns>
    IReadOnlyList<FaceRect> Detect(byte[] bgrData, int width, int height);
}

/// <summary>
/// Optional: detector that exposes confidence scores for the last detection (for debug overlays).
/// </summary>
public interface IFaceDetectorWithScores : IFaceDetector
{
    /// <summary>
    /// Scores for the last Detect() call, in same order as returned face list. Empty if not supported.
    /// </summary>
    IReadOnlyList<float> GetLastScores();
}

/// <summary>
/// Optional: detector that exposes preprocess info for debug overlay (e.g. "preproc=A, order=BGR, layout=NCHW").
/// </summary>
public interface IFaceDetectorDebugInfo
{
    string GetLastPreprocessInfo();
}

public readonly struct FaceRect
{
    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;

    public FaceRect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}
