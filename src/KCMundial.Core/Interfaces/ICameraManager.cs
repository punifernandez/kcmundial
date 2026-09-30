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
    /// Start preview on the given camera device. Frames are pushed via the callback on a background thread.
    /// </summary>
    /// <param name="device">Camera device from GetCamerasAsync()</param>
    /// <param name="onFrame">Called with BGR frame (clone it if you need to keep it); do not block.</param>
    /// <param name="cancellationToken">Stops the preview when cancelled.</param>
    /// <param name="preferPortraitFormats">If true (p. ej. pantalla vertical), preferir resolución 9:16.</param>
    Task StartPreviewAsync(CameraDevice device, Action<byte[], int, int> onFrame, CancellationToken cancellationToken = default, bool preferPortraitFormats = false);

    /// <summary>
    /// Stop the current preview. Idempotent.
    /// </summary>
    Task StopPreviewAsync();

    /// <summary>
    /// Capture a single high-resolution still from the current camera. Prefer 4K if stable.
    /// Call after preview is running. Returns BGR + dimensions for saving and composition, or null on failure.
    /// </summary>
    Task<CaptureResult?> CaptureStillAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether a preview is currently active.
    /// </summary>
    bool IsPreviewActive { get; }

    /// <summary>
    /// Raised when the camera disconnects or errors (e.g. unplugged).
    /// </summary>
    event EventHandler<string>? CameraError;
}
