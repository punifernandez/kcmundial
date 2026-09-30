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
    /// <summary>Tiempo mínimo en 4K antes de tomar la foto, para que la cámara reenfoque y ajuste la exposición.</summary>
    private const int HighResSettleMs = 1000;
    /// <summary>Cuadros candidatos al disparar: se queda el más nítido.</summary>
    private const int HighResCandidates = 4;
    private static readonly TimeSpan HighResGrabTimeout = TimeSpan.FromSeconds(3);
    /// <summary>Si nadie dispara, volver solo al formato del preview.</summary>
    private static readonly TimeSpan HighResAutoRevert = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _opLock = new(1, 1);
    private volatile bool _suspendPreviewFrames;
    // Modo 4K (desde la cuenta regresiva hasta la foto)
    private volatile bool _highResActive;
    private readonly Stopwatch _highResClock = new();
    private int _highResGeneration;
    private byte[]? _highResScratch;
    private volatile FrameGrabber? _grabber;
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
            if (!_highResActive)
            {
                lock (_snapshotLock)
                {
                    if (!CopyBgra(bitmap, ref _latestFrame, out var w, out var h)) return;
                    _latestFrameWidth = w;
                    _latestFrameHeight = h;
                    callback?.Invoke(_latestFrame!, w, h);
                }
                return;
            }

            // En 4K: los cuadros candidatos para la foto van enteros; el preview recibe uno de cada dos a media resolución.
            var grabber = _grabber;
            if (grabber != null)
            {
                byte[]? full = null;
                if (CopyBgra(bitmap, ref full, out var fw, out var fh))
                    grabber.Offer(full!, fw, fh);
                return;
            }
            if (_frameCount % 2 != 0) return;
            lock (_snapshotLock)
            {
                if (!CopyBgra(bitmap, ref _highResScratch, out var w, out var h)) return;
                HalveBgra(_highResScratch!, w, h, ref _latestFrame, out var hw, out var hh);
                _latestFrameWidth = hw;
                _latestFrameHeight = hh;
                callback?.Invoke(_latestFrame!, hw, hh);
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

    /// <summary>
    /// Pasa la cámara a su formato más grande (se llama al empezar la cuenta regresiva): el preview sigue en vivo y
    /// la cámara tiene tiempo de enfocar y ajustar la exposición antes de la foto.
    /// </summary>
    public async Task PrepareHighResAsync()
    {
        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnterHighResCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error("PrepareHighRes: could not switch to high-res", ex);
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async Task<CaptureResult?> CaptureStillAsync(bool highRes = true, CancellationToken cancellationToken = default)
    {
        await _opLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CaptureResult? result = null;
            if (highRes)
            {
                try
                {
                    await EnterHighResCoreAsync().ConfigureAwait(false);
                    if (_highResActive)
                        result = await GrabSharpestAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Error("CaptureStill: high-res capture failed, using preview frame", ex);
                }
                finally
                {
                    await ExitHighResCoreAsync().ConfigureAwait(false);
                }
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

    /// <summary>Cambia al formato máximo si se puede y todavía no está. Requiere _opLock.</summary>
    private async Task EnterHighResCoreAsync()
    {
        if (_highResActive) return;
        MediaFrameFormat? previewFormat, maxFormat;
        lock (_lock)
        {
            previewFormat = _previewFormat;
            maxFormat = _maxCaptureFormat;
        }
        if (_mediaCapture == null || previewFormat == null || maxFormat == null) return;
        if (!_canSetFormat || Pixels(maxFormat) <= Pixels(previewFormat))
        {
            _logger?.Info("HighRes: not available (shared mode or preview already at max)");
            return;
        }

        var sw = Stopwatch.StartNew();
        _highResActive = true;
        _highResClock.Restart();
        if (!await SwitchFormatAsync(maxFormat).ConfigureAwait(false))
        {
            _highResActive = false;
            await SwitchFormatAsync(previewFormat).ConfigureAwait(false);
            return;
        }
        _logger?.Info($"HighRes: switched to {Describe(maxFormat)} in {sw.ElapsedMilliseconds} ms");

        var generation = Interlocked.Increment(ref _highResGeneration);
        _ = Task.Run(async () =>
        {
            await Task.Delay(HighResAutoRevert).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _highResGeneration) || !_highResActive) return;
            await _opLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation == Volatile.Read(ref _highResGeneration))
                {
                    _logger?.Warn("HighRes: no capture happened, returning to preview format");
                    await ExitHighResCoreAsync().ConfigureAwait(false);
                }
            }
            finally { _opLock.Release(); }
        });
    }

    /// <summary>Vuelve al formato del preview. Requiere _opLock.</summary>
    private async Task ExitHighResCoreAsync()
    {
        if (!_highResActive) return;
        Interlocked.Increment(ref _highResGeneration);
        _highResActive = false;
        _grabber = null;
        MediaFrameFormat? previewFormat;
        lock (_lock) previewFormat = _previewFormat;
        if (previewFormat != null && !await SwitchFormatAsync(previewFormat).ConfigureAwait(false))
        {
            _logger?.Error("HighRes: could not restore preview format, restarting camera");
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
        lock (_snapshotLock) _highResScratch = null;
    }

    /// <summary>Espera a que la cámara se estabilice y devuelve el más nítido de los próximos cuadros.</summary>
    private async Task<CaptureResult?> GrabSharpestAsync(CancellationToken cancellationToken)
    {
        var wait = HighResSettleMs - (int)_highResClock.ElapsedMilliseconds;
        if (wait > 0)
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        var grabber = new FrameGrabber(HighResCandidates);
        _grabber = grabber;
        try
        {
            var winner = await Task.WhenAny(grabber.Completion, Task.Delay(HighResGrabTimeout, cancellationToken)).ConfigureAwait(false);
            if (winner != grabber.Completion)
                _logger?.Warn($"CaptureStill: only {grabber.Count} high-res frame(s) arrived in {HighResGrabTimeout.TotalSeconds:0} s");
        }
        finally
        {
            _grabber = null;
        }

        var best = grabber.Best;
        _logger?.Info(best != null
            ? $"CaptureStill: high-res {best.Width}x{best.Height}, {grabber.Count} candidates, sharpness [{string.Join(", ", grabber.Scores.Select(x => x.ToString("0.0")))}], " +
              $"settled {_highResClock.ElapsedMilliseconds - sw.ElapsedMilliseconds} ms, grab {sw.ElapsedMilliseconds} ms"
            : "CaptureStill: no high-res frame");
        return best;
    }

    /// <summary>
    /// Detiene el reader actual, cambia el formato de la fuente y arranca un reader nuevo.
    /// Requiere _opLock. Devuelve false si algo falló.
    /// </summary>
    private async Task<bool> SwitchFormatAsync(MediaFrameFormat format)
    {
        MediaCapture? capture;
        MediaFrameSource? source;
        MediaFrameReader? old;
        lock (_lock)
        {
            capture = _mediaCapture;
            source = _currentFrameSource;
            old = _frameReader;
        }
        if (capture == null || source == null) return false;
        try
        {
            _suspendPreviewFrames = true;
            if (old != null)
            {
                old.FrameArrived -= OnFrameArrived;
                await old.StopAsync().AsTask().ConfigureAwait(false);
            }
            await source.SetFormatAsync(format).AsTask().ConfigureAwait(false);
            var reader = await CreateBgraReaderAsync(capture, source).ConfigureAwait(false);
            reader.FrameArrived += OnFrameArrived;
            lock (_lock) _frameReader = reader;
            old?.Dispose();
            var status = await reader.StartAsync().AsTask().ConfigureAwait(false);
            if (status != MediaFrameReaderStartStatus.Success)
            {
                _logger?.Warn($"SwitchFormat: reader start {status} for {Describe(format)}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error($"SwitchFormat: {Describe(format)} failed", ex);
            return false;
        }
        finally
        {
            _suspendPreviewFrames = false;
        }
    }

    /// <summary>Reduce un cuadro BGRA a la mitad (un píxel de cada 2×2), para el preview en modo 4K.</summary>
    private static void HalveBgra(byte[] source, int width, int height, ref byte[]? target, out int outW, out int outH)
    {
        outW = width / 2;
        outH = height / 2;
        var size = outW * outH * 4;
        if (target == null || target.Length != size) target = new byte[size];
        var src = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.AsSpan());
        var dst = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(target.AsSpan());
        for (var y = 0; y < outH; y++)
        {
            var srcRow = src.Slice(y * 2 * width, width);
            var dstRow = dst.Slice(y * outW, outW);
            for (var x = 0; x < outW; x++)
                dstRow[x] = srcRow[x * 2];
        }
    }

    /// <summary>Junta los próximos N cuadros y se queda con el más nítido.</summary>
    private sealed class FrameGrabber
    {
        private readonly int _wanted;
        private readonly object _lock = new();
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private double _bestScore = double.MinValue;

        public FrameGrabber(int wanted) => _wanted = wanted;

        public Task Completion => _done.Task;
        public CaptureResult? Best { get; private set; }
        public List<double> Scores { get; } = new();
        public int Count { get { lock (_lock) return Scores.Count; } }

        public void Offer(byte[] bgra, int width, int height)
        {
            var score = Sharpness(bgra, width, height);
            lock (_lock)
            {
                if (Scores.Count >= _wanted) return;
                Scores.Add(score);
                if (score > _bestScore)
                {
                    _bestScore = score;
                    Best = new CaptureResult { Bgra = bgra, Width = width, Height = height, IsHighRes = true };
                }
                if (Scores.Count >= _wanted) _done.TrySetResult();
            }
        }

        /// <summary>Nitidez: energía de bordes del canal verde en la zona central (donde está la cara).</summary>
        private static double Sharpness(byte[] bgra, int width, int height)
        {
            const int step = 3;
            var x0 = width / 4;
            var x1 = width * 3 / 4 - step;
            var y0 = height / 4;
            var y1 = height * 3 / 4 - step;
            long sum = 0;
            long n = 0;
            var stride = width * 4;
            for (var y = y0; y < y1; y += step)
            {
                var row = y * stride;
                for (var x = x0; x < x1; x += step)
                {
                    int g = bgra[row + x * 4 + 1];
                    sum += Math.Abs(bgra[row + (x + step) * 4 + 1] - g) + Math.Abs(bgra[row + step * stride + x * 4 + 1] - g);
                    n++;
                }
            }
            return n == 0 ? 0 : (double)sum / n;
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
