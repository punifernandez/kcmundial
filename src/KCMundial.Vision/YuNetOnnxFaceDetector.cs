using System.Diagnostics;
using KCMundial.Core.Interfaces;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace KCMundial.Vision;

/// <summary>
/// ONNX face detection (YuNet). Input shape and layout read from model; preprocess options configurable.
/// </summary>
public sealed class YuNetOnnxFaceDetector : IFaceDetectorWithScores, IFaceDetectorDebugInfo
{
    private readonly string _modelPath;
    private readonly float _scoreThreshold;
    private readonly IAppLogger? _logger;
    private InferenceSession? _session;
    private readonly object _sessionLock = new();
    private bool _disposed;
    private volatile IReadOnlyList<float> _lastScores = Array.Empty<float>();

    /// <summary>Model input height (from metadata).</summary>
    private int _inputH = 320;
    /// <summary>Model input width (from metadata).</summary>
    private int _inputW = 320;
    /// <summary>True = NCHW [N,C,H,W], false = NHWC [N,H,W,C].</summary>
    private bool _isNchw = true;

    /// <summary>False = 0..255, true = /255.</summary>
    public bool UseNormalizedInput { get; set; }
    /// <summary>False = BGR, true = RGB (swap channels).</summary>
    public bool UseRgbOrder { get; set; }

    /// <summary>After this many consecutive failures, mark unhealthy (fallback to Haar).</summary>
    private const int FailureThreshold = 5;
    private int _consecutiveFailures;
    private DateTime _lastErrorLogTime = DateTime.MinValue;
    private const double ErrorThrottleSeconds = 3.0;

    /// <summary>If true, caller should use fallback detector.</summary>
    public bool IsUnhealthy => _consecutiveFailures >= FailureThreshold;

    /// <summary>Last preprocess description for debug overlay (e.g. "preproc=A, order=BGR, layout=NCHW").</summary>
    public string LastPreprocessInfo { get; private set; } = "";

    public YuNetOnnxFaceDetector(string modelPath, float scoreThreshold = 0.4f, IAppLogger? logger = null)
    {
        _modelPath = modelPath ?? throw new ArgumentNullException(nameof(modelPath));
        _scoreThreshold = scoreThreshold;
        _logger = logger;
    }

    private InferenceSession GetOrCreateSession()
    {
        if (_session != null) return _session;
        lock (_sessionLock)
        {
            if (_session != null) return _session;
            if (_disposed) throw new ObjectDisposedException(nameof(YuNetOnnxFaceDetector));
            if (!File.Exists(_modelPath))
                throw new InvalidOperationException($"YuNet model not found: {_modelPath}");
            _session = new InferenceSession(_modelPath);
            var inputMeta = _session.InputMetadata.First();
            var dims = inputMeta.Value.Dimensions;
            if (dims != null && dims.Length >= 4)
            {
                // [N, C, H, W] => C at index 1; [N, H, W, C] => C at index 3
                if (dims[1] == 3)
                {
                    _isNchw = true;
                    _inputH = dims[2];
                    _inputW = dims[3];
                }
                else if (dims[3] == 3)
                {
                    _isNchw = false;
                    _inputH = dims[1];
                    _inputW = dims[2];
                }
                else
                {
                    _inputH = dims[2];
                    _inputW = dims[3];
                }
            }
            _logger?.Info($"YuNet ONNX model loaded: {_modelPath}");
            _logger?.Info($"YuNet input size: {_inputW}x{_inputH}, layout={(_isNchw ? "NCHW" : "NHWC")}, preproc={(UseNormalizedInput ? "0..1" : "0..255")}, order={(UseRgbOrder ? "RGB" : "BGR")}");
            return _session;
        }
    }

    public IReadOnlyList<FaceRect> Detect(byte[] bgrData, int width, int height)
    {
        if (_disposed) return Array.Empty<FaceRect>();
        if (bgrData == null || bgrData.Length < width * height * 3)
            return Array.Empty<FaceRect>();

        var sw = Stopwatch.StartNew();
        try
        {
            InferenceSession session;
            try
            {
                session = GetOrCreateSession();
            }
            catch (InvalidOperationException)
            {
                _lastScores = Array.Empty<float>();
                return Array.Empty<FaceRect>();
            }

            LastPreprocessInfo = $"preproc={(UseNormalizedInput ? "B" : "A")}, order={(UseRgbOrder ? "RGB" : "BGR")}, layout={(_isNchw ? "NCHW" : "NHWC")}";
            var scaleX = (double)_inputW / width;
            var scaleY = (double)_inputH / height;
            var inputTensor = Preprocess(bgrData, width, height, _inputW, _inputH, _isNchw, UseNormalizedInput, UseRgbOrder);

            var inputName = session.InputNames[0];
            var inputValue = NamedOnnxValue.CreateFromTensor(inputName, inputTensor);

            IReadOnlyList<FaceRect> result;
            using (var outputs = session.Run(new[] { inputValue }))
            {
                // YuNet may have multiple outputs; pick the 2D one with most columns (detection tensor is usually [N, 14] or [N, 15])
                var outputTensor = (Tensor<float>?)null;
                var outputName = "";
                var maxCols = 0;
                foreach (var outVal in outputs)
                {
                    var t = outVal.AsTensor<float>();
                    if (t != null && t.Rank == 2 && t.Dimensions[1] >= 5 && t.Dimensions[1] > maxCols)
                    {
                        outputTensor = t;
                        outputName = outVal.Name;
                        maxCols = t.Dimensions[1];
                    }
                }
                if (outputTensor == null)
                {
                    _lastScores = Array.Empty<float>();
                    _consecutiveFailures++;
                    ThrottledError("YuNet: no 2D output tensor with >=5 columns found");
                    return Array.Empty<FaceRect>();
                }

                var rows = outputTensor.Dimensions[0];
                var cols = outputTensor.Dimensions[1];
                var scoreIndex = cols - 1;
                var list = new List<FaceRect>();
                var scores = new List<float>();
                var rawLogCount = 0;
                var loggedRawOnce = false;

                for (var r = 0; r < rows; r++)
                {
                    var score = outputTensor[r, scoreIndex];
                    if (score < _scoreThreshold) continue;

                    var x = outputTensor[r, 0];
                    var y = outputTensor[r, 1];
                    var w = outputTensor[r, 2];
                    var h = outputTensor[r, 3];

                    // Many YuNet exports use normalized 0..1 bbox; if so, values are <= 1.x
                    var maxB = Math.Max(Math.Max(x, y), Math.Max(w, h));
                    if (maxB <= 1.5f)
                    {
                        x *= _inputW;
                        y *= _inputH;
                        w *= _inputW;
                        h *= _inputH;
                    }

                    if (!loggedRawOnce && score > 0.1f)
                    {
                        loggedRawOnce = true;
                        var sb = new System.Text.StringBuilder();
                        for (var c = 0; c < Math.Min(cols, 8); c++)
                            sb.Append(outputTensor[r, c].ToString("F3")).Append(c < cols - 1 ? "," : "");
                        _logger?.Info($"YuNet output \"{outputName}\" shape=[{rows},{cols}] first row (first 8 cols): {sb}");
                    }
                    if (rawLogCount < 3 && score > 0.1f)
                    {
                        _logger?.Info($"YuNet raw det[{rawLogCount}] score={score:F3} box=({x:F1},{y:F1},{w:F1},{h:F1})");
                        rawLogCount++;
                    }

                    var origX = (int)Math.Round(x / scaleX);
                    var origY = (int)Math.Round(y / scaleY);
                    var origW = (int)Math.Round(w / scaleX);
                    var origH = (int)Math.Round(h / scaleY);
                    origX = Math.Max(0, Math.Min(origX, width - 1));
                    origY = Math.Max(0, Math.Min(origY, height - 1));
                    origW = Math.Max(1, Math.Min(origW, width - origX));
                    origH = Math.Max(1, Math.Min(origH, height - origY));

                    list.Add(new FaceRect(origX, origY, origW, origH));
                    scores.Add(score);
                }

                _lastScores = scores;
                result = list;
                _consecutiveFailures = 0;
            }

            sw.Stop();
            _logger?.Info($"YuNet detect: {result.Count} face(s), score>={_scoreThreshold}, {sw.ElapsedMilliseconds} ms");
            return result;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            ThrottledError($"YuNet detect failed: {ex.Message}");
            _lastScores = Array.Empty<float>();
            return Array.Empty<FaceRect>();
        }
    }

    private void ThrottledError(string message)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastErrorLogTime).TotalSeconds < ErrorThrottleSeconds)
            return;
        _lastErrorLogTime = now;
        _logger?.Warn($"{message} (failures={_consecutiveFailures}, throttle=1 per {ErrorThrottleSeconds}s)");
        if (_consecutiveFailures >= FailureThreshold)
            _logger?.Warn($"YuNet marked unhealthy after {FailureThreshold} failures; use Haar fallback.");
    }

    public IReadOnlyList<float> GetLastScores() => _lastScores;

    public string GetLastPreprocessInfo() => LastPreprocessInfo;

    private static DenseTensor<float> Preprocess(byte[] bgr, int srcW, int srcH, int dstW, int dstH, bool nchw, bool normalize, bool rgbOrder)
    {
        var scaleX = (double)srcW / dstW;
        var scaleY = (double)srcH / dstH;
        var div = normalize ? 255f : 1f;

        if (nchw)
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, dstH, dstW });
            for (var dy = 0; dy < dstH; dy++)
            for (var dx = 0; dx < dstW; dx++)
            {
                var sx = Math.Min((int)(dx * scaleX), srcW - 1);
                var sy = Math.Min((int)(dy * scaleY), srcH - 1);
                var idx = (sy * srcW + sx) * 3;
                var b = bgr[idx] / div;
                var g = bgr[idx + 1] / div;
                var r = bgr[idx + 2] / div;
                if (rgbOrder)
                {
                    tensor[0, 0, dy, dx] = r;
                    tensor[0, 1, dy, dx] = g;
                    tensor[0, 2, dy, dx] = b;
                }
                else
                {
                    tensor[0, 0, dy, dx] = b;
                    tensor[0, 1, dy, dx] = g;
                    tensor[0, 2, dy, dx] = r;
                }
            }
            return tensor;
        }
        else
        {
            var tensor = new DenseTensor<float>(new[] { 1, dstH, dstW, 3 });
            for (var dy = 0; dy < dstH; dy++)
            for (var dx = 0; dx < dstW; dx++)
            {
                var sx = Math.Min((int)(dx * scaleX), srcW - 1);
                var sy = Math.Min((int)(dy * scaleY), srcH - 1);
                var idx = (sy * srcW + sx) * 3;
                var b = bgr[idx] / div;
                var g = bgr[idx + 1] / div;
                var r = bgr[idx + 2] / div;
                if (rgbOrder)
                {
                    tensor[0, dy, dx, 0] = r;
                    tensor[0, dy, dx, 1] = g;
                    tensor[0, dy, dx, 2] = b;
                }
                else
                {
                    tensor[0, dy, dx, 0] = b;
                    tensor[0, dy, dx, 1] = g;
                    tensor[0, dy, dx, 2] = r;
                }
            }
            return tensor;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
