using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;

namespace KCMundial.Camera;

/// <summary>
/// Usa MediaCapture primero; si no encuentra cámaras (lista vacía), enumera con DirectShow
/// para que en PCs donde Windows.Devices.Enumeration falla igual aparezcan la Brio y otras USB.
/// </summary>
public sealed class FallbackCameraManager : ICameraManager
{
    private readonly MediaCaptureCameraManager _mediaCapture;
    private readonly CameraManager _dshow;
    private readonly IAppLogger? _logger;
    private bool _useDshow;
    private IReadOnlyList<CameraDevice>? _lastDshowList;

    public FallbackCameraManager(IAppLogger? logger = null)
    {
        _logger = logger;
        _mediaCapture = new MediaCaptureCameraManager(logger);
        _dshow = new CameraManager(logger);
        _mediaCapture.CameraError += (_, msg) => CameraError?.Invoke(this, msg);
        _dshow.CameraError += (_, msg) => CameraError?.Invoke(this, msg);
    }

    public bool IsPreviewActive => _useDshow ? _dshow.IsPreviewActive : _mediaCapture.IsPreviewActive;

    public event EventHandler<string>? CameraError;

    public async Task<IReadOnlyList<CameraDevice>> GetCamerasAsync()
    {
        var list = await _mediaCapture.GetCamerasAsync().ConfigureAwait(false);
        if (list.Count > 0)
        {
            _useDshow = false;
            _lastDshowList = null;
            _logger?.Info($"FallbackCameraManager: MediaCapture found {list.Count} camera(s)");
            return list;
        }
        _logger?.Info("FallbackCameraManager: MediaCapture returned 0, trying DirectShow");
#pragma warning disable CS0618
        var dshowList = await _dshow.GetCamerasAsync().ConfigureAwait(false);
#pragma warning restore CS0618
        _lastDshowList = dshowList;
        if (dshowList.Count == 0)
        {
            _logger?.Warn("FallbackCameraManager: DirectShow also returned 0 cameras");
            return dshowList;
        }
        var result = new List<CameraDevice>(dshowList.Count);
        for (int i = 0; i < dshowList.Count; i++)
        {
            result.Add(new CameraDevice { Id = $"dshow_{i}", Name = dshowList[i].Name });
        }
        _logger?.Info($"FallbackCameraManager: DirectShow found {result.Count} camera(s)");
        return result;
    }

    public async Task StartPreviewAsync(CameraDevice device, Action<byte[], int, int> onFrame, CancellationToken cancellationToken = default, bool preferPortraitFormats = false)
    {
        if (device.Id.StartsWith("dshow_", StringComparison.Ordinal) && int.TryParse(device.Id.AsSpan(6), out int idx) && _lastDshowList != null && idx >= 0 && idx < _lastDshowList.Count)
        {
            _useDshow = true;
            var dshowDevice = _lastDshowList[idx];
            // CameraManager abre por índice; su Id debe ser "0", "1", etc.
            var byIndex = new CameraDevice { Id = idx.ToString(), Name = dshowDevice.Name };
#pragma warning disable CS0618
            await _dshow.StartPreviewAsync(byIndex, onFrame, cancellationToken, preferPortraitFormats).ConfigureAwait(false);
#pragma warning restore CS0618
            return;
        }
        _useDshow = false;
        await _mediaCapture.StartPreviewAsync(device, onFrame, cancellationToken, preferPortraitFormats).ConfigureAwait(false);
    }

    public async Task StopPreviewAsync()
    {
        await _mediaCapture.StopPreviewAsync().ConfigureAwait(false);
        await _dshow.StopPreviewAsync().ConfigureAwait(false);
    }

    public Task<CaptureResult?> CaptureStillAsync(CancellationToken cancellationToken = default)
    {
        if (_useDshow)
            return _dshow.CaptureStillAsync(cancellationToken);
        return _mediaCapture.CaptureStillAsync(cancellationToken);
    }

    public void Dispose()
    {
        _mediaCapture.Dispose();
        _dshow.Dispose();
    }
}
