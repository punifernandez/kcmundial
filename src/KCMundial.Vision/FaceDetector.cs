using System.Runtime.InteropServices;
using KCMundial.Core.Interfaces;
using OpenCvSharp;

namespace KCMundial.Vision;

/// <summary>
/// Face detection using OpenCV Haar Cascade. Loads haarcascade_frontalface_default.xml from the given path.
/// </summary>
public sealed class FaceDetector : IFaceDetector
{
    private CascadeClassifier? _cascade;
    private bool _disposed;

    public FaceDetector(string cascadePath)
    {
        if (File.Exists(cascadePath))
            _cascade = new CascadeClassifier(cascadePath);
    }

    public IReadOnlyList<FaceRect> Detect(byte[] bgrData, int width, int height)
    {
        if (_cascade == null || _disposed)
            return Array.Empty<FaceRect>();

        using var mat = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(bgrData, 0, mat.Data, bgrData.Length);
        
        // For very high resolution images (4K+), scale down for better face detection performance
        // Target ~1920x1080 max for detection, then scale results back
        double scaleFactor = 1.0;
        Mat? detectionMat = null;
        Mat? gray = null;
        
        try
        {
            if (width > 1920 || height > 1080)
            {
                // Scale down proportionally to max 1920 width or 1080 height
                scaleFactor = Math.Min(1920.0 / width, 1080.0 / height);
                var newWidth = (int)(width * scaleFactor);
                var newHeight = (int)(height * scaleFactor);
                detectionMat = new Mat();
                Cv2.Resize(mat, detectionMat, new OpenCvSharp.Size(newWidth, newHeight));
            }
            else
            {
                detectionMat = mat;
            }
            
            gray = new Mat();
            Cv2.CvtColor(detectionMat, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);
            
            // Adjust min face size based on detection resolution (proportional to 60x60 at 1920x1080)
            var detectionWidth = detectionMat.Width;
            var detectionHeight = detectionMat.Height;
            var minFaceSize = Math.Max(30, (int)(60 * Math.Min(detectionWidth / 1920.0, detectionHeight / 1080.0)));
            
            var faces = _cascade.DetectMultiScale(gray, 1.1, 5, HaarDetectionTypes.ScaleImage, new OpenCvSharp.Size(minFaceSize, minFaceSize));
            var list = new List<FaceRect>(faces.Length);
            
            // Scale face rectangles back to original resolution
            foreach (var r in faces)
            {
                var scaledX = (int)(r.X / scaleFactor);
                var scaledY = (int)(r.Y / scaleFactor);
                var scaledWidth = (int)(r.Width / scaleFactor);
                var scaledHeight = (int)(r.Height / scaleFactor);
                list.Add(new FaceRect(scaledX, scaledY, scaledWidth, scaledHeight));
            }
            
            return list;
        }
        finally
        {
            // Only dispose if we created a new mat (scaled version)
            if (scaleFactor != 1.0 && detectionMat != null && detectionMat != mat)
            {
                detectionMat.Dispose();
            }
            gray?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cascade?.Dispose();
        _cascade = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
