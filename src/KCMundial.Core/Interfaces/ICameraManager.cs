using KCMundial.Core.Models;

namespace KCMundial.Core.Interfaces;

/// <summary>
/// Camera enumeration, preview streaming, and high-res still capture.
/// All camera work runs off the UI thread.
/// </summary>
public interface ICameraManager : IDisposable
{
    /// <summary>
    /// Enumerate available cameras. Safe to call from any thread.
    /// </summary>
    Task<IReadOnlyList<CameraDevice>> GetCamerasAsync();

    /// <summary>
    /// Start preview on the given camera device. Frames are pushed via the callback on a background thread
    /// as BGRA 32bpp (stride = width * 4). The buffer is reused: copy what you need before returning, and do not block.
    /// </summary>
    Task StartPreviewAsync(CameraDevice device, Action<byte[], int, int> onFrame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop the current preview. Idempotent.
    /// </summary>
    Task StopPreviewAsync();

    /// <summary>
    /// Switch the camera to its largest format ahead of the capture (e.g. when the countdown starts) so it can
    /// refocus and settle exposure while the preview keeps running. No-op if unsupported.
    /// </summary>
    Task PrepareHighResAsync();

    /// <summary>
    /// Capture a still from the current camera. With <paramref name="highRes"/> the camera briefly switches to its
    /// largest format; if that fails the latest preview frame is returned. Null only if nothing is available.
    /// </summary>
    Task<CaptureResult?> CaptureStillAsync(bool highRes = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a preview is currently active.
    /// </summary>
    bool IsPreviewActive { get; }

    /// <summary>
    /// Raised when the camera disconnects or errors (e.g. unplugged).
    /// </summary>
    event EventHandler<string>? CameraError;
}
