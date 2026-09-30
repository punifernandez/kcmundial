using KCMundial.Core.Interfaces;

namespace KCMundial.Vision;

/// <summary>
/// Uses primary detector (e.g. YuNet); on unhealthy or exception falls back to secondary (e.g. Haar).
/// No error spam: failures are throttled inside primary; once unhealthy we use fallback silently.
/// </summary>
public sealed class FaceDetectorWithFallback : IFaceDetectorWithScores, IFaceDetectorDebugInfo
{
    private readonly IFaceDetector _primary;
    private readonly IFaceDetector _fallback;
    private readonly IAppLogger? _logger;
    private bool _useFallback;
    private bool _disposed;

    public FaceDetectorWithFallback(IFaceDetector primary, IFaceDetector fallback, IAppLogger? logger = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _logger = logger;
    }

    public IReadOnlyList<FaceRect> Detect(byte[] bgrData, int width, int height)
    {
        if (_disposed) return Array.Empty<FaceRect>();

        if (!_useFallback && _primary is YuNetOnnxFaceDetector yunet && yunet.IsUnhealthy)
        {
            _useFallback = true;
            _logger?.Info("FaceDetectorWithFallback: YuNet unhealthy, switching to Haar for this session");
        }

        if (!_useFallback)
        {
            try
            {
                var result = _primary.Detect(bgrData, width, height);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Warn($"FaceDetectorWithFallback: primary failed ({ex.Message}), using Haar");
                _useFallback = true;
            }
        }

        return _fallback.Detect(bgrData, width, height);
    }

    public IReadOnlyList<float> GetLastScores()
    {
        if (!_useFallback && _primary is IFaceDetectorWithScores ps)
            return ps.GetLastScores();
        if (_fallback is IFaceDetectorWithScores fs)
            return fs.GetLastScores();
        return Array.Empty<float>();
    }

    public string GetLastPreprocessInfo()
    {
        if (!_useFallback && _primary is IFaceDetectorDebugInfo pd)
            return pd.GetLastPreprocessInfo();
        if (_fallback is IFaceDetectorDebugInfo fd)
            return fd.GetLastPreprocessInfo();
        return _useFallback ? "Haar" : "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _primary.Dispose();
        _fallback.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
