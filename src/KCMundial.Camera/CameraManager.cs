using System.Diagnostics;
using System.Runtime.InteropServices;
using DirectShowLib;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;
using OpenCvSharp;

namespace KCMundial.Camera;

public sealed class CameraManager : ICameraManager
{
    private readonly object _lock = new();
    private readonly IAppLogger? _logger;
    private VideoCapture? _capture;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private int _currentCameraIndex = -1;
    private Action<byte[], int, int>? _lastOnFrame;
    private bool _disposed;

    // Latest frame snapshot for capture
    private readonly object _snapshotLock = new();
    private byte[]? _latestSnapshot;
    private int _latestWidth;
    private int _latestHeight;

    public CameraManager(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public bool IsPreviewActive
    {
        get { lock (_lock) return _capture != null && _previewTask != null; }
    }

    public event EventHandler<string>? CameraError;

    /// <summary>
    /// Enumerate cameras via DirectShow (same order as OpenCV DSHOW backend).
    /// Returns real device names (e.g. "Logitech BRIO", "Integrated Camera").
    /// NOTE: This is deprecated - use MediaCaptureCameraManager instead.
    /// </summary>
    [Obsolete("Use MediaCaptureCameraManager instead")]
    public Task<IReadOnlyList<CameraDevice>> GetCamerasAsync()
    {
        var sw = Stopwatch.StartNew();
        _logger?.Info("GetCameras: enumerating DirectShow video input devices");
        var list = new List<CameraDevice>();
        try
        {
            var dshowDevices = DsDevice.GetDevicesOfCat(FilterCategory.VideoInputDevice);
            for (int i = 0; i < dshowDevices.Length; i++)
            {
                var device = dshowDevices[i];
                var name = device.Name ?? $"Camera {i}";
                var id = device.DevicePath ?? $"index_{i}";
                list.Add(new CameraDevice { Id = id, Name = name });
                _logger?.Info($"GetCameras: index={i} name=\"{name}\" id={id.Substring(0, Math.Min(8, id.Length))}...");
            }
            sw.Stop();
            _logger?.Info($"GetCameras: found {list.Count} camera(s) in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Error("GetCameras: DirectShow enumeration failed", ex);
            _logger?.Info("GetCameras: fallback to OpenCV probe 0..9");
            for (int i = 0; i < 10; i++)
            {
                try
                {
                    using var probe = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                    if (probe.IsOpened())
                    {
                        list.Add(new CameraDevice { Id = $"index_{i}", Name = $"Camera {i}" });
                        _logger?.Info($"GetCameras: fallback index={i}");
                    }
                }
                catch { /* ignore */ }
            }
        }
        return Task.FromResult<IReadOnlyList<CameraDevice>>(list);
    }

    [Obsolete("Use MediaCaptureCameraManager instead")]
    public async Task StartPreviewAsync(CameraDevice device, Action<byte[], int, int> onFrame, CancellationToken cancellationToken = default)
    {
        // Legacy implementation - extract index from device name for backward compatibility
        // This should not be used - MediaCaptureCameraManager is the correct implementation
        var cameraIndex = 0;
        if (int.TryParse(device.Id, out var parsedIndex))
            cameraIndex = parsedIndex;
        
        _logger?.Info($"StartPreview: stopping previous preview (camera {cameraIndex} requested)");
        
        // MUST await StopPreviewAsync to avoid race conditions
        await StopPreviewAsync().ConfigureAwait(false);
        
        // Wait 250ms after stop to allow USB driver release
        await Task.Delay(250).ConfigureAwait(false);
        
        _logger?.Info($"StartPreview: opening camera {cameraIndex}");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        VideoCapture? capture = null;
        try
        {
            var sw = Stopwatch.StartNew();
            
            // Force DirectShow backend for webcams
            capture = await Task.Run(() =>
            {
                _logger?.Info($"StartPreview: Task.Run creating VideoCapture({cameraIndex}, DSHOW)");
                return new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            }).ConfigureAwait(false);
            
            sw.Stop();
            _logger?.Info($"StartPreview: VideoCapture({cameraIndex}) created in {sw.ElapsedMilliseconds} ms");
            
            if (!capture.IsOpened())
            {
                _logger?.Warn($"StartPreview: camera {cameraIndex} failed to open");
                capture.Dispose();
                RaiseCameraError("No se pudo abrir la cámara.");
                return;
            }

            // Force stable preview mode: MJPG FourCC + 1920x1080 + 30fps
            capture.Set(VideoCaptureProperties.FourCC, FourCC.FromString("MJPG"));
            capture.Set(VideoCaptureProperties.FrameWidth, 1920);
            capture.Set(VideoCaptureProperties.FrameHeight, 1080);
            capture.Set(VideoCaptureProperties.Fps, 30);

            _logger?.Info($"StartPreview: camera index={cameraIndex} opened successfully, starting preview loop");
            lock (_lock)
            {
                _capture = capture;
                _currentCameraIndex = cameraIndex;
                _lastOnFrame = onFrame;
                _previewCts = cts;
                _previewTask = Task.Run(() => RunPreviewLoopAsync(capture, onFrame, cts.Token));
            }
            _logger?.Info($"StartPreview: camera {cameraIndex} preview loop started");

            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"StartPreview: camera {cameraIndex} exception", ex);
            capture?.Dispose();
            RaiseCameraError($"Error al iniciar cámara: {ex.Message}");
        }
    }

    private async Task RunPreviewLoopAsync(VideoCapture capture, Action<byte[], int, int> onFrame, CancellationToken token)
    {
        _logger?.Info($"RunPreviewLoopAsync: started for camera");
        using var frame = new Mat();
        using var bgra = new Mat();
        var consecutiveErrors = 0;
        const int maxConsecutiveErrors = 50;
        
        while (!token.IsCancellationRequested && !_disposed)
        {
            try
            {
                if (token.IsCancellationRequested) break;
                
                var readSuccess = false;
                try
                {
                    readSuccess = capture.Read(frame);
                }
                catch (Exception readEx)
                {
                    _logger?.Warn($"RunPreviewLoopAsync: Read() exception: {readEx.Message}");
                    consecutiveErrors++;
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        _logger?.Error($"RunPreviewLoopAsync: too many consecutive errors, stopping");
                        RaiseCameraError("Error leyendo frames de la cámara");
                        break;
                    }
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }
                
                if (!readSuccess || frame.Empty())
                {
                    consecutiveErrors++;
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        _logger?.Error($"RunPreviewLoopAsync: too many empty frames ({consecutiveErrors}), stopping");
                        RaiseCameraError("La cámara no está enviando frames");
                        break;
                    }
                    if (token.IsCancellationRequested) break;
                    await Task.Delay(33, token).ConfigureAwait(false);
                    continue;
                }

                consecutiveErrors = 0;
                
                if (token.IsCancellationRequested) break;
                // Read() bloquea hasta el próximo cuadro: no hace falta demorar el loop.
                Cv2.CvtColor(frame, bgra, ColorConversionCodes.BGR2BGRA);
                var w = bgra.Width;
                var h = bgra.Height;
                var length = w * h * 4;
                lock (_snapshotLock)
                {
                    if (_latestSnapshot == null || _latestSnapshot.Length != length)
                        _latestSnapshot = new byte[length];
                    if (bgra.IsContinuous())
                        Marshal.Copy(bgra.Data, _latestSnapshot, 0, length);
                    else
                        for (var y = 0; y < h; y++)
                            Marshal.Copy(bgra.Ptr(y), _latestSnapshot, y * w * 4, w * 4);
                    _latestWidth = w;
                    _latestHeight = h;
                    onFrame(_latestSnapshot, w, h);
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("RunPreviewLoopAsync: cancelled");
                break;
            }
            catch (ObjectDisposedException)
            {
                _logger?.Info("RunPreviewLoopAsync: capture disposed");
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                _logger?.Warn($"RunPreviewLoopAsync: exception: {ex.Message} (error count: {consecutiveErrors})");
                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    _logger?.Error($"RunPreviewLoopAsync: too many errors, stopping");
                    if (!_disposed && !token.IsCancellationRequested) RaiseCameraError($"Error en preview: {ex.Message}");
                    break;
                }
                await Task.Delay(100, token).ConfigureAwait(false);
                continue;
            }

        }
        _logger?.Info("RunPreviewLoopAsync: exited");
    }

    private const int StopPreviewTimeoutMs = 500;

    public async Task StopPreviewAsync()
    {
        Task? toWait = null;
        VideoCapture? captureToDispose = null;
        lock (_lock)
        {
            if (_previewCts != null)
            {
                try { _previewCts.Cancel(); } catch { /* ignore */ }
            }
            toWait = _previewTask;
            captureToDispose = _capture;
            _previewTask = null;
            _previewCts = null;
            _capture = null;
            _currentCameraIndex = -1;
            _lastOnFrame = null;
        }
        
        // Wait for loop to exit first (with timeout)
        if (toWait != null)
        {
            try
            {
                var completed = await Task.WhenAny(toWait, Task.Delay(StopPreviewTimeoutMs)).ConfigureAwait(false);
                if (completed != toWait)
                    _logger?.Info($"StopPreview: previous loop did not exit in {StopPreviewTimeoutMs} ms, continuing anyway");
                else
                    _logger?.Info("StopPreview: previous loop exited cleanly");
            }
            catch (OperationCanceledException) { _logger?.Info("StopPreview: loop cancelled"); }
            catch (Exception ex) { _logger?.Info($"StopPreview: wait ended: {ex.Message}"); }
        }
        
        // Dispose capture AFTER loop exits
        if (captureToDispose != null)
        {
            try
            {
                _logger?.Info("StopPreview: releasing and disposing capture");
                captureToDispose.Release();
            }
            catch (Exception ex) { _logger?.Warn($"StopPreview: Release() exception: {ex.Message}"); }
            try
            {
                captureToDispose.Dispose();
            }
            catch (Exception ex) { _logger?.Warn($"StopPreview: Dispose() exception: {ex.Message}"); }
        }
        
        // Clear snapshot
        lock (_snapshotLock)
        {
            _latestSnapshot = null;
            _latestWidth = 0;
            _latestHeight = 0;
        }
        
        _logger?.Info("StopPreview: stopped");
    }

    /// <summary>DirectShow es solo respaldo: devuelve el último cuadro del preview (sin cambio a 4K).</summary>
    public Task<CaptureResult?> CaptureStillAsync(bool highRes = true, CancellationToken cancellationToken = default)
    {
        lock (_snapshotLock)
        {
            if (_latestSnapshot == null)
            {
                _logger?.Warn("CaptureStillAsync: no snapshot available");
                return Task.FromResult<CaptureResult?>(null);
            }
            _logger?.Info($"CaptureStillAsync: returning preview frame {_latestWidth}x{_latestHeight}");
            return Task.FromResult<CaptureResult?>(new CaptureResult { Bgra = (byte[])_latestSnapshot.Clone(), Width = _latestWidth, Height = _latestHeight });
        }
    }

    private void RaiseCameraError(string message)
    {
        CameraError?.Invoke(this, message);
    }

    public void Dispose()
    {
        _disposed = true;
        StopPreviewAsync().GetAwaiter().GetResult();
    }
}
