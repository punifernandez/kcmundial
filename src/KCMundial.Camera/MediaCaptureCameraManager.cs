using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Devices;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace KCMundial.Camera;

/// <summary>
/// MediaCapture-based camera manager that uses DeviceInformation.Id for reliable device selection.
/// </summary>
public sealed class MediaCaptureCameraManager : ICameraManager
{
    private readonly object _lock = new();
    private readonly IAppLogger? _logger;
    private MediaCapture? _mediaCapture;
    private MediaFrameReader? _frameReader;
    private CancellationTokenSource? _previewCts;
    private Task? _previewTask;
    private CameraDevice? _currentDevice;
    private Action<byte[], int, int>? _lastOnFrame;
    private bool _disposed;

    private readonly object _snapshotLock = new();

    // Formatos del preview y de la foto
    private MediaFrameFormat? _previewFormat;
    private MediaFrameFormat? _maxCaptureFormat;
    private MediaFrameSource? _currentFrameSource;

    public MediaCaptureCameraManager(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public bool IsPreviewActive
    {
        get { lock (_lock) return _mediaCapture != null && _frameReader != null && _previewTask != null; }
    }

    public event EventHandler<string>? CameraError;

    /// <summary>
    /// Enumerate cameras using Windows.Devices.Enumeration.
    /// Gets real device names (e.g., "Logitech BRIO", "Logitech C920", "Integrated Camera").
    /// </summary>
    public async Task<IReadOnlyList<CameraDevice>> GetCamerasAsync()
    {
        var sw = Stopwatch.StartNew();
        _logger?.Info("GetCamerasAsync: enumerating Windows.Devices.Enumeration video capture devices");
        var list = new List<CameraDevice>();
        try
        {
            // Use a selector that includes additional properties for better device names
            var selector = MediaDevice.GetVideoCaptureSelector();
            
            // Request additional properties to get better device names
            var additionalProperties = new[] 
            { 
                "System.Devices.FriendlyName",
                "System.ItemNameDisplay",
                "System.Devices.DeviceDescription1",
                "System.Devices.DeviceDescription2",
                "System.Devices.HardwareIds",
                "System.Devices.Manufacturer",
                "System.Devices.ModelName"
            };
            
            var devices = await DeviceInformation.FindAllAsync(selector, additionalProperties);
            
            foreach (var device in devices)
            {
                // Try multiple properties to get the best device name
                string name = "Unknown Camera";
                string? bestName = null;
                
                // Priority 1: System.Devices.FriendlyName (most descriptive)
                if (device.Properties != null && device.Properties.TryGetValue("System.Devices.FriendlyName", out var friendlyNameObj) && friendlyNameObj is string friendlyName && !string.IsNullOrWhiteSpace(friendlyName))
                {
                    bestName = friendlyName;
                }
                
                // Priority 2: Device.Name property
                if (string.IsNullOrWhiteSpace(bestName) && !string.IsNullOrWhiteSpace(device.Name))
                {
                    bestName = device.Name;
                }
                
                // Priority 3: System.ItemNameDisplay
                if (string.IsNullOrWhiteSpace(bestName) && device.Properties != null && device.Properties.TryGetValue("System.ItemNameDisplay", out var displayNameObj) && displayNameObj is string displayName && !string.IsNullOrWhiteSpace(displayName))
                {
                    bestName = displayName;
                }
                
                // Priority 4: Try to build name from Manufacturer + ModelName
                if (string.IsNullOrWhiteSpace(bestName) && device.Properties != null)
                {
                    string? manufacturer = null;
                    string? modelName = null;
                    
                    if (device.Properties.TryGetValue("System.Devices.Manufacturer", out var mfrObj) && mfrObj is string mfr && !string.IsNullOrWhiteSpace(mfr))
                    {
                        manufacturer = mfr;
                    }
                    
                    if (device.Properties.TryGetValue("System.Devices.ModelName", out var modelObj) && modelObj is string model && !string.IsNullOrWhiteSpace(model))
                    {
                        modelName = model;
                    }
                    
                    if (!string.IsNullOrWhiteSpace(manufacturer) || !string.IsNullOrWhiteSpace(modelName))
                    {
                        if (!string.IsNullOrWhiteSpace(manufacturer) && !string.IsNullOrWhiteSpace(modelName))
                        {
                            bestName = $"{manufacturer} {modelName}";
                        }
                        else if (!string.IsNullOrWhiteSpace(manufacturer))
                        {
                            bestName = manufacturer;
                        }
                        else if (!string.IsNullOrWhiteSpace(modelName))
                        {
                            bestName = modelName;
                        }
                    }
                }
                
                // Priority 5: System.Devices.DeviceDescription1 or DeviceDescription2
                if (string.IsNullOrWhiteSpace(bestName) && device.Properties != null)
                {
                    if (device.Properties.TryGetValue("System.Devices.DeviceDescription1", out var desc1Obj) && desc1Obj is string desc1 && !string.IsNullOrWhiteSpace(desc1))
                    {
                        bestName = desc1;
                    }
                    else if (device.Properties.TryGetValue("System.Devices.DeviceDescription2", out var desc2Obj) && desc2Obj is string desc2 && !string.IsNullOrWhiteSpace(desc2))
                    {
                        bestName = desc2;
                    }
                }
                
                // Priority 6: Try to extract from HardwareIds
                if (string.IsNullOrWhiteSpace(bestName) && device.Properties != null && device.Properties.TryGetValue("System.Devices.HardwareIds", out var hwIdsObj) && hwIdsObj is string[] hwIds && hwIds.Length > 0)
                {
                    // Hardware IDs are like "USB\\VID_046D&PID_085B&REV_0011" - extract VID/PID info
                    foreach (var hwId in hwIds)
                    {
                        if (!string.IsNullOrWhiteSpace(hwId))
                        {
                            // Try to identify common camera manufacturers from VID
                            if (hwId.Contains("VID_046D")) // Logitech
                            {
                                // Try to identify model from PID
                                if (hwId.Contains("PID_085B")) bestName = "Logitech BRIO";
                                else if (hwId.Contains("PID_082D")) bestName = "Logitech C920";
                                else if (hwId.Contains("PID_0825")) bestName = "Logitech C910";
                                else if (hwId.Contains("PID_0826")) bestName = "Logitech C930e";
                                else bestName = "Logitech Camera";
                            }
                            else if (hwId.Contains("VID_0C45")) // Microdia
                            {
                                bestName = "Microdia Camera";
                            }
                            else if (hwId.Contains("VID_13D3")) // IMC Networks
                            {
                                bestName = "Integrated Camera";
                            }
                            
                            if (!string.IsNullOrWhiteSpace(bestName))
                                break;
                        }
                    }
                }
                
                // Use the best name found, or fallback to device.Name or "Unknown Camera"
                name = bestName ?? device.Name ?? "Unknown Camera";
                
                // Clean up the name - remove common generic suffixes/prefixes
                name = name.Trim();
                
                // Check if name is still generic and try to improve it
                if (IsGenericName(name))
                {
                    _logger?.Warn($"GetCamerasAsync: detected generic name \"{name}\", attempting to improve");
                    
                    // Try to get more info by checking if it's a USB device
                    bool isUsb = false;
                    bool isBuiltIn = false;
                    
                    if (device.Properties != null)
                    {
                        if (device.Properties.TryGetValue("System.Devices.HardwareIds", out var hwIdsObj2) && hwIdsObj2 is string[] hwIds2)
                        {
                            foreach (var hwId2 in hwIds2)
                            {
                                if (!string.IsNullOrWhiteSpace(hwId2))
                                {
                                    if (hwId2.Contains("USB\\")) isUsb = true;
                                    if (hwId2.Contains("VID_13D3") || hwId2.Contains("VID_0BDA")) isBuiltIn = true;
                                }
                            }
                        }
                        
                        // Try to get container ID to determine if it's built-in
                        if (device.Properties.TryGetValue("System.Devices.ContainerId", out var containerIdObj))
                        {
                            // Built-in cameras often have specific container patterns
                            var containerStr = containerIdObj?.ToString() ?? "";
                            if (containerStr.Contains("00000000-0000-0000-0000-000000000000") || string.IsNullOrWhiteSpace(containerStr))
                            {
                                // This might indicate a built-in device
                            }
                        }
                    }
                    
                    // If we couldn't get a better name, at least try to make it more descriptive
                    if (name == "Unknown Camera" || name == "Camera" || name.StartsWith("Cámara "))
                    {
                        // Don't change it if it already has descriptive info
                        if (!name.Contains("(") && !name.Contains("USB") && !name.Contains("built-in"))
                        {
                            if (isBuiltIn)
                                name = name.Replace("Camera", "Integrated Camera").Replace("Cámara", "Cámara integrada");
                            else if (isUsb)
                                name = name.Replace("Camera", "USB Camera").Replace("Cámara", "Cámara USB");
                        }
                    }
                }
                
                var id = device.Id ?? string.Empty;
                list.Add(new CameraDevice { Id = id, Name = name });
                var idPreview = id.Length > 8 ? id.Substring(0, 8) + "..." : id;
                _logger?.Info($"GetCamerasAsync: final name=\"{name}\" id={idPreview}");
            }
            sw.Stop();
            _logger?.Info($"GetCamerasAsync: found {list.Count} camera(s) in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger?.Error("GetCamerasAsync: enumeration failed", ex);
        }
        return list;
    }

    // ---------------------------------------------------------------------------------
    // Preview + captura
    //
    // El preview corre en un formato liviano (≤1920×1080) y el reader le pide a Media Foundation
    // los cuadros ya convertidos a BGRA (decodifica MJPG/NV12 por nosotros, sin conversiones en C#).
    // Para la foto, la cámara pasa un instante a su formato más grande (Brio: 4K), toma un cuadro
    // y vuelve al formato del preview.
    // ---------------------------------------------------------------------------------

    private static readonly HashSet<string> DecodableSubtypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "NV12", "YUY2", "MJPG", "RGB24", "RGB32", "ARGB32", "BGRA8", "BGR8"
    };
    private const int HighResSkipFrames = 2;
    private static readonly TimeSpan HighResTimeout = TimeSpan.FromSeconds(4);

    private readonly SemaphoreSlim _opLock = new(1, 1);
    private volatile bool _suspendPreviewFrames;
    private byte[]? _latestFrame;
    private int _latestFrameWidth;
    private int _latestFrameHeight;
    private bool _canSetFormat;

    public Task StartPreviewAsync(CameraDevice device, Action<byte[], int, int> onFrame, CancellationToken cancellationToken = default)
        => RunLockedAsync(() => StartPreviewCoreAsync(device, onFrame, cancellationToken));

    public Task StopPreviewAsync() => RunLockedAsync(StopPreviewCoreAsync);

    private async Task RunLockedAsync(Func<Task> action)
    {
        await _opLock.WaitAsync().ConfigureAwait(false);
        try { await action().ConfigureAwait(false); }
        finally { _opLock.Release(); }
    }

    private async Task StartPreviewCoreAsync(CameraDevice device, Action<byte[], int, int> onFrame, CancellationToken cancellationToken)
    {
        _logger?.Info($"StartPreviewAsync: requested {device.DisplayName} (StableKey={device.StableKey})");
        await StopPreviewCoreAsync().ConfigureAwait(false);
        // Darle tiempo al driver USB a liberar la cámara anterior.
        await Task.Delay(250, cancellationToken).ConfigureAwait(false);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        MediaCapture? capture = null;
        MediaFrameReader? reader = null;
        try
        {
            var sw = Stopwatch.StartNew();
            (capture, _canSetFormat) = await InitializeCaptureAsync(device.DeviceId).ConfigureAwait(false);
            _logger?.Info($"StartPreviewAsync: MediaCapture initialized in {sw.ElapsedMilliseconds} ms (canSetFormat={_canSetFormat})");

            var source = SelectFrameSource(capture);
            if (source == null)
            {
                capture.Dispose();
                RaiseCameraError("No se encontró fuente de video en la cámara.");
                return;
            }

            var formats = source.SupportedFormats.Where(IsUsableFormat).ToList();
            foreach (var f in formats.OrderByDescending(Pixels).Take(12))
                _logger?.Info($"StartPreviewAsync:   format {Describe(f)}");

            var previewFormat = SelectPreviewFormat(formats) ?? source.CurrentFormat;
            var maxFormat = SelectMaxFormat(formats);
            _logger?.Info($"StartPreviewAsync: preview={Describe(previewFormat)}, still={(maxFormat != null ? Describe(maxFormat) : "n/a")}");

            if (_canSetFormat)
            {
                try { await source.SetFormatAsync(previewFormat).AsTask().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    _logger?.Warn($"StartPreviewAsync: SetFormatAsync failed ({ex.Message}), using current format");
                    previewFormat = source.CurrentFormat;
                }
            }
            else
            {
                previewFormat = source.CurrentFormat;
            }

            reader = await CreateBgraReaderAsync(capture, source).ConfigureAwait(false);
            reader.FrameArrived += OnFrameArrived;

            lock (_lock)
            {
                _mediaCapture = capture;
                _frameReader = reader;
                _currentFrameSource = source;
                _previewFormat = previewFormat;
                _maxCaptureFormat = maxFormat;
                _currentDevice = device;
                _lastOnFrame = onFrame;
                _previewCts = cts;
                _previewTask = Task.CompletedTask;
            }

            var status = await reader.StartAsync().AsTask().ConfigureAwait(false);
            if (status != MediaFrameReaderStartStatus.Success)
            {
                _logger?.Warn($"StartPreviewAsync: frame reader start failed with status {status}");
                await StopPreviewCoreAsync().ConfigureAwait(false);
                RaiseCameraError("No se pudo iniciar la lectura de la cámara.");
                return;
            }
            _frameCount = 0;
            _lastFrameLogTime = DateTime.MinValue;
            _logger?.Info($"StartPreviewAsync: active camera = {device.DisplayName}");
        }
        catch (Exception ex)
        {
            _logger?.Error($"StartPreviewAsync: camera \"{device.DisplayName}\" exception", ex);
            lock (_lock)
            {
                if (ReferenceEquals(_mediaCapture, capture)) { _mediaCapture = null; _frameReader = null; _previewTask = null; }
            }
            if (reader != null) reader.FrameArrived -= OnFrameArrived;
            reader?.Dispose();
            capture?.Dispose();
            RaiseCameraError($"Error al iniciar cámara: {ex.Message}");
        }
    }

    private async Task<(MediaCapture Capture, bool CanSetFormat)> InitializeCaptureAsync(string deviceId)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl
            }).AsTask().ConfigureAwait(false);
            return (capture, true);
        }
        catch (Exception ex)
        {
            // Otra app tiene la cámara: se puede ver, pero no cambiar de formato (sin foto 4K).
            _logger?.Warn($"InitializeCapture: ExclusiveControl failed ({ex.Message}), trying SharedReadOnly");
            capture.Dispose();
            capture = new MediaCapture();
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly
            }).AsTask().ConfigureAwait(false);
            return (capture, false);
        }
    }

    private static async Task<MediaFrameReader> CreateBgraReaderAsync(MediaCapture capture, MediaFrameSource source)
    {
        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8).AsTask().ConfigureAwait(false);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        return reader;
    }

    /// <summary>Fuente de color con el formato más grande (preferir VideoPreview/Record sobre otras).</summary>
    private MediaFrameSource? SelectFrameSource(MediaCapture capture)
    {
        return capture.FrameSources.Values
            .Where(fs => fs.Info.SourceKind == MediaFrameSourceKind.Color &&
                         (fs.Info.MediaStreamType == MediaStreamType.VideoPreview || fs.Info.MediaStreamType == MediaStreamType.VideoRecord))
            .OrderByDescending(fs => fs.SupportedFormats.Where(IsUsableFormat).Select(Pixels).DefaultIfEmpty(0).Max())
            .ThenBy(fs => fs.Info.MediaStreamType == MediaStreamType.VideoRecord ? 0 : 1)
            .FirstOrDefault();
    }

    private static bool IsUsableFormat(MediaFrameFormat f)
    {
        var v = f.VideoFormat;
        if (v == null || v.Width < 320 || v.Height < 240) return false;
        if (!DecodableSubtypes.Contains(f.Subtype)) return false; // descarta H264/HEVC/L8 etc.
        return Fps(f) >= 5;
    }

    /// <summary>Preview: el más grande que entre en 1920×1080 a ≥24 fps; preferir formatos sin comprimir.</summary>
    private static MediaFrameFormat? SelectPreviewFormat(List<MediaFrameFormat> formats)
    {
        static bool FitsPreview(MediaFrameFormat f) =>
            Math.Max(f.VideoFormat.Width, f.VideoFormat.Height) <= 1920 && Math.Min(f.VideoFormat.Width, f.VideoFormat.Height) <= 1080;

        return formats.Where(f => FitsPreview(f) && Fps(f) >= 24)
                   .OrderByDescending(Pixels)
                   .ThenBy(f => f.Subtype.Equals("MJPG", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                   .ThenByDescending(Fps)
                   .FirstOrDefault()
               ?? formats.Where(FitsPreview).OrderByDescending(Pixels).ThenByDescending(Fps).FirstOrDefault();
    }

    /// <summary>Foto: el formato con más píxeles (cualquier fps).</summary>
    private static MediaFrameFormat? SelectMaxFormat(List<MediaFrameFormat> formats) =>
        formats.OrderByDescending(Pixels).ThenByDescending(Fps).FirstOrDefault();

    private static long Pixels(MediaFrameFormat f) => (long)f.VideoFormat.Width * f.VideoFormat.Height;
    private static double Fps(MediaFrameFormat f) => f.FrameRate is { Denominator: > 0 } r ? (double)r.Numerator / r.Denominator : 0;
    private static string Describe(MediaFrameFormat f) => $"{f.VideoFormat.Width}x{f.VideoFormat.Height} {f.Subtype} {Fps(f):0.#}fps";

    private int _frameCount;
    private DateTime _lastFrameLogTime = DateTime.MinValue;

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_disposed || _suspendPreviewFrames || _previewCts?.Token.IsCancellationRequested == true)
            return;
        try
        {
            using var frame = sender.TryAcquireLatestFrame();
            var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap == null) return;

            _frameCount++;
            var now = DateTime.UtcNow;
            if ((now - _lastFrameLogTime).TotalSeconds >= 30)
            {
                _logger?.Info($"Preview: {bitmap.PixelWidth}x{bitmap.PixelHeight}, frame #{_frameCount}");
                _lastFrameLogTime = now;
            }

            var callback = _lastOnFrame;
            lock (_snapshotLock)
            {
                if (!CopyBgra(bitmap, ref _latestFrame, out var w, out var h)) return;
                _latestFrameWidth = w;
                _latestFrameHeight = h;
                callback?.Invoke(_latestFrame!, w, h);
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"OnFrameArrived: {ex.Message}");
        }
    }

    /// <summary>Copia un SoftwareBitmap BGRA a un array empaquetado (stride = ancho × 4), reusando el array si alcanza.</summary>
    private static bool CopyBgra(SoftwareBitmap bitmap, ref byte[]? target, out int width, out int height)
    {
        width = bitmap.PixelWidth;
        height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0) return false;

        SoftwareBitmap? converted = null;
        try
        {
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                bitmap = converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);

            var packed = width * 4;
            var size = packed * height;
            if (target == null || target.Length != size) target = new byte[size];

            int stride;
            using (var locked = bitmap.LockBuffer(BitmapBufferAccessMode.Read))
                stride = locked.GetPlaneDescription(0).Stride;

            if (stride == packed)
            {
                bitmap.CopyToBuffer(target.AsBuffer());
            }
            else
            {
                var padded = new byte[stride * height];
                bitmap.CopyToBuffer(padded.AsBuffer());
                for (var y = 0; y < height; y++)
                    System.Buffer.BlockCopy(padded, y * stride, target, y * packed, packed);
            }
            return true;
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private async Task StopPreviewCoreAsync()
    {
        MediaFrameReader? reader;
        MediaCapture? capture;
        lock (_lock)
        {
            try { _previewCts?.Cancel(); } catch { /* ignore */ }
            reader = _frameReader;
            capture = _mediaCapture;
            _previewTask = null;
            _previewCts = null;
            _frameReader = null;
            _mediaCapture = null;
            _currentDevice = null;
            _lastOnFrame = null;
            _previewFormat = null;
            _maxCaptureFormat = null;
            _currentFrameSource = null;
        }

        if (reader != null)
        {
            try
            {
                reader.FrameArrived -= OnFrameArrived;
                await reader.StopAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception ex) { _logger?.Warn($"StopPreview: reader stop: {ex.Message}"); }
            reader.Dispose();
        }
        if (capture != null)
        {
            try { capture.Dispose(); }
            catch (Exception ex) { _logger?.Warn($"StopPreview: MediaCapture dispose: {ex.Message}"); }
        }
        lock (_snapshotLock)
        {
            _latestFrame = null;
            _latestFrameWidth = 0;
            _latestFrameHeight = 0;
        }
        if (reader != null || capture != null)
            _logger?.Info("StopPreview: stopped");
    }

    public async Task<CaptureResult?> CaptureStillAsync(bool highRes = true, CancellationToken cancellationToken = default)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CaptureResult? result = null;
            if (highRes)
            {
                try { result = await CaptureHighResCoreAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { _logger?.Error("CaptureStill: high-res capture failed, using preview frame", ex); }
            }
            return result ?? SnapshotPreviewFrame();
        }
        finally
        {
            _opLock.Release();
        }
    }

    private CaptureResult? SnapshotPreviewFrame()
    {
        lock (_snapshotLock)
        {
            if (_latestFrame == null)
            {
                _logger?.Warn("CaptureStill: no preview frame available");
                return null;
            }
            _logger?.Info($"CaptureStill: using preview frame {_latestFrameWidth}x{_latestFrameHeight}");
            return new CaptureResult { Bgra = (byte[])_latestFrame.Clone(), Width = _latestFrameWidth, Height = _latestFrameHeight };
        }
    }

    private async Task<CaptureResult?> CaptureHighResCoreAsync(CancellationToken cancellationToken)
    {
        MediaCapture? capture;
        MediaFrameReader? previewReader;
        MediaFrameSource? source;
        MediaFrameFormat? previewFormat, maxFormat;
        lock (_lock)
        {
            capture = _mediaCapture;
            previewReader = _frameReader;
            source = _currentFrameSource;
            previewFormat = _previewFormat;
            maxFormat = _maxCaptureFormat;
        }
        if (capture == null || previewReader == null || source == null || previewFormat == null || maxFormat == null)
            return null;
        if (!_canSetFormat || Pixels(maxFormat) <= Pixels(previewFormat))
        {
            _logger?.Info("CaptureStill: high-res not available (shared mode or preview already at max)");
            return null;
        }

        var sw = Stopwatch.StartNew();
        _suspendPreviewFrames = true;
        CaptureResult? result = null;
        try
        {
            await previewReader.StopAsync().AsTask().ConfigureAwait(false);
            await source.SetFormatAsync(maxFormat).AsTask().ConfigureAwait(false);
            var switchMs = sw.ElapsedMilliseconds;

            using var stillReader = await CreateBgraReaderAsync(capture, source).ConfigureAwait(false);
            var tcs = new TaskCompletionSource<CaptureResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = 0;
            stillReader.FrameArrived += (s, _) =>
            {
                try
                {
                    using var frame = s.TryAcquireLatestFrame();
                    var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
                    // Los primeros cuadros después de cambiar de formato pueden venir mal expuestos.
                    if (bitmap == null || Interlocked.Increment(ref seen) <= HighResSkipFrames || tcs.Task.IsCompleted) return;
                    byte[]? data = null;
                    if (CopyBgra(bitmap, ref data, out var w, out var h))
                        tcs.TrySetResult(new CaptureResult { Bgra = data!, Width = w, Height = h, IsHighRes = true });
                }
                catch (Exception ex) { tcs.TrySetException(ex); }
            };

            var status = await stillReader.StartAsync().AsTask().ConfigureAwait(false);
            if (status == MediaFrameReaderStartStatus.Success)
            {
                var winner = await Task.WhenAny(tcs.Task, Task.Delay(HighResTimeout, cancellationToken)).ConfigureAwait(false);
                if (winner == tcs.Task) result = await tcs.Task.ConfigureAwait(false);
                else _logger?.Warn("CaptureStill: timed out waiting for high-res frame");
            }
            else
            {
                _logger?.Warn($"CaptureStill: still reader start failed ({status})");
            }
            await stillReader.StopAsync().AsTask().ConfigureAwait(false);
            _logger?.Info($"CaptureStill: high-res {(result != null ? $"{result.Width}x{result.Height}" : "failed")}, format switch {switchMs} ms, total {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            await RestorePreviewAsync(capture, source, previewFormat).ConfigureAwait(false);
            _suspendPreviewFrames = false;
        }
        return result;
    }

    /// <summary>Vuelve al formato del preview con un reader nuevo. Si falla, reinicia la cámara completa.</summary>
    private async Task RestorePreviewAsync(MediaCapture capture, MediaFrameSource source, MediaFrameFormat previewFormat)
    {
        try
        {
            await source.SetFormatAsync(previewFormat).AsTask().ConfigureAwait(false);
            var reader = await CreateBgraReaderAsync(capture, source).ConfigureAwait(false);
            reader.FrameArrived += OnFrameArrived;
            MediaFrameReader? old;
            lock (_lock)
            {
                old = _frameReader;
                _frameReader = reader;
            }
            if (old != null)
            {
                old.FrameArrived -= OnFrameArrived;
                old.Dispose();
            }
            var status = await reader.StartAsync().AsTask().ConfigureAwait(false);
            if (status != MediaFrameReaderStartStatus.Success)
                throw new InvalidOperationException($"preview reader restart: {status}");
        }
        catch (Exception ex)
        {
            _logger?.Error("CaptureStill: could not restore preview, restarting camera", ex);
            CameraDevice? device;
            Action<byte[], int, int>? onFrame;
            lock (_lock)
            {
                device = _currentDevice;
                onFrame = _lastOnFrame;
            }
            if (device != null && onFrame != null)
                await StartPreviewCoreAsync(device, onFrame, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool IsGenericName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;

        var lower = name.ToLowerInvariant();
        if (lower == "camera" || lower == "cámara" || lower == "unknown camera" || lower == "cámara desconocida")
            return true;
        if (Regex.IsMatch(lower, @"^(camera|cámara)\s*\d+$"))
            return true;
        if (lower.Contains("usb video device") || lower.Contains("usb2.0 camera") || lower.Contains("usb camera"))
            return true;
        return false;
    }

    private void RaiseCameraError(string message)
    {
        CameraError?.Invoke(this, message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPreviewAsync().GetAwaiter().GetResult();
    }
}
