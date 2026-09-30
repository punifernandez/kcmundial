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

    // Latest frame snapshot for capture
    private readonly object _snapshotLock = new();
    private byte[]? _latestSnapshot;
    private int _latestWidth;
    private int _latestHeight;
    
    // Format management for preview vs capture
    private MediaFrameFormat? _previewFormat;
    private MediaFrameFormat? _maxCaptureFormat;
    private MediaFrameSource? _currentFrameSource;
    private bool _force9_16;

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

    public async Task StartPreviewAsync(CameraDevice device, Action<byte[], int, int> onFrame, CancellationToken cancellationToken = default, bool preferPortraitFormats = false)
    {
        var requestedDisplayName = device.DisplayName;
        var requestedStableKey = device.StableKey;
        var requestedDeviceId = device.DeviceId;
        var deviceIdPreview = requestedDeviceId.Length > 20 ? requestedDeviceId.Substring(0, 20) + "..." : requestedDeviceId;
        _logger?.Info($"StartPreviewAsync: Requested camera = {requestedDisplayName} (StableKey={requestedStableKey}, DeviceId={deviceIdPreview})");
        _logger?.Info($"StartPreviewAsync: Stopping current = {(_currentDevice != null ? _currentDevice.DisplayName + " (" + _currentDevice.StableKey + ")" : "none")}");
        
        // MUST await StopPreviewAsync to avoid race conditions
        await StopPreviewAsync().ConfigureAwait(false);
        
        // Wait 250ms after stop to allow driver release
        await Task.Delay(250).ConfigureAwait(false);
        
        _logger?.Info($"StartPreviewAsync: Opening by DeviceId (DisplayName={requestedDisplayName}, StableKey={requestedStableKey})");
        _force9_16 = preferPortraitFormats;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        MediaCapture? capture = null;
        MediaFrameReader? reader = null;
        bool canSetFormat = true; // false if we fell back to SharedReadOnly
        try
        {
            var sw = Stopwatch.StartNew();
            capture = new MediaCapture();

            // Open by DeviceId only (never by index)
            var settingsExclusive = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = device.DeviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.ExclusiveControl
            };
            try
            {
                await capture.InitializeAsync(settingsExclusive).AsTask().ConfigureAwait(false);
                _logger?.Info("StartPreviewAsync: initialized with ExclusiveControl");
            }
            catch (Exception exInit)
            {
                _logger?.Warn($"StartPreviewAsync: ExclusiveControl failed ({exInit.Message}), trying SharedReadOnly");
                capture.Dispose();
                capture = new MediaCapture();
                var settingsShared = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = device.DeviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly
                };
                await capture.InitializeAsync(settingsShared).AsTask().ConfigureAwait(false);
                canSetFormat = false;
                _logger?.Info("StartPreviewAsync: initialized with SharedReadOnly (format change disabled)");
            }

            sw.Stop();
            _logger?.Info($"StartPreviewAsync: MediaCapture initialized in {sw.ElapsedMilliseconds} ms");
            
            // Create frame reader for low-latency preview - try ALL frame sources to find best one
            var sourceGroups = capture.FrameSources.Where(fs => fs.Value.Info.MediaStreamType == MediaStreamType.VideoPreview || fs.Value.Info.MediaStreamType == MediaStreamType.VideoRecord).ToList();
            if (sourceGroups.Count == 0)
            {
                _logger?.Warn("StartPreviewAsync: no video frame source found");
                capture.Dispose();
                RaiseCameraError("No se encontró fuente de video en la cámara.");
                return;
            }

            // Find best frame source: prefer ones with uncompressed formats (NV12, YUY2, RGB) over compressed (H264, H264ES)
            MediaFrameSource? bestFrameSource = null;
            int bestFormatCount = 0;
            
            foreach (var kvp in sourceGroups)
            {
                var fs = kvp.Value;
                var uncompressedFormats = fs.SupportedFormats.Where(f => 
                    f.Subtype == "NV12" || f.Subtype == "YUY2" || f.Subtype == "RGB24" || 
                    f.Subtype == "RGB32" || f.Subtype == "BGRA8" || f.Subtype == "BGR8").Count();
                
                if (uncompressedFormats > bestFormatCount)
                {
                    bestFormatCount = uncompressedFormats;
                    bestFrameSource = fs;
                }
            }
            
            // If no source with uncompressed formats found, use first one
            if (bestFrameSource == null)
            {
                bestFrameSource = sourceGroups[0].Value;
                _logger?.Warn("StartPreviewAsync: no frame source with uncompressed formats found, using first available");
            }
            else
            {
                _logger?.Info($"StartPreviewAsync: selected frame source with {bestFormatCount} uncompressed formats");
            }
            
            var frameSource = bestFrameSource;
            
            // Log all available formats for debugging
            _logger?.Info($"StartPreviewAsync: available formats count={frameSource.SupportedFormats.Count}");
            foreach (var fmt in frameSource.SupportedFormats.Take(10))
            {
                var vf = fmt.VideoFormat;
                _logger?.Info($"StartPreviewAsync:   format {vf.Width}x{vf.Height} subtype={fmt.Subtype}");
            }
            
            // Get all formats, filter out weird formats (square, too small, etc.)
            var supportedFormats = frameSource.SupportedFormats.ToList();
            
            // Filter: reasonable formats (not square, not too small, reasonable aspect ratio)
            bool IsReasonableFormat(MediaFrameFormat f)
            {
                var v = f.VideoFormat;
                if (v.Width < 320 || v.Height < 240) return false; // Too small
                var ar = v.Width > 0 ? (double)v.Width / v.Height : 0;
                if (preferPortraitFormats)
                {
                    if (ar < 0.35 || ar > 2.5) return false; // Permitir 9:16 (portrait) y landscape
                }
                else
                {
                    if (ar < 1.0 || ar > 2.5) return false; // Solo landscape
                }
                if (Math.Abs(ar - 1.0) < 0.1) return false; // Rechazar cuadrados
                if (f.Subtype == "L8") return false;
                return true;
            }
            
            // Prefer uncompressed formats (NV12, YUY2, RGB) over compressed (H264, H264ES)
            bool IsUncompressedFormat(MediaFrameFormat f)
            {
                return f.Subtype == "NV12" || f.Subtype == "YUY2" || f.Subtype == "RGB24" || 
                       f.Subtype == "RGB32" || f.Subtype == "BGRA8" || f.Subtype == "BGR8";
            }
            
            var reasonableFormats = supportedFormats.Where(IsReasonableFormat).ToList();
            var preferredFormats = reasonableFormats
                .Where(f => f.VideoFormat.Width >= 640 && f.VideoFormat.Height >= 480)
                .ToList();
            
            if (preferredFormats.Count == 0)
            {
                _logger?.Warn("StartPreviewAsync: no formats >= 640x480, using reasonable formats");
                preferredFormats = reasonableFormats;
            }
            
            if (preferredFormats.Count == 0)
            {
                _logger?.Warn("StartPreviewAsync: no reasonable formats found, using all formats");
                preferredFormats = supportedFormats;
            }

            // Prioritize MAXIMUM resolution (width * height), prefer 16:9 o 9:16 según orientación
            double AspectRatio(MediaFrameFormat f)
            {
                var v = f.VideoFormat;
                return v.Width > 0 ? (double)v.Width / v.Height : 0;
            }
            bool Is16_9(MediaFrameFormat f)
            {
                var ar = AspectRatio(f);
                return ar >= 1.6 && ar <= 1.85;
            }
            const double AR_9_16 = 9.0 / 16.0; // 0.5625
            bool Is9_16(MediaFrameFormat f)
            {
                var ar = AspectRatio(f);
                return ar >= AR_9_16 - 0.1 && ar <= AR_9_16 + 0.1;
            }
            
            // PREVIEW: landscape max 1920x1080 (16:9); portrait max 1080x1920 (9:16)
            var previewFormats = preferPortraitFormats
                ? preferredFormats.Where(f => f.VideoFormat.Width <= 1080 && f.VideoFormat.Height <= 1920).ToList()
                : preferredFormats.Where(f => f.VideoFormat.Width <= 1920 && f.VideoFormat.Height <= 1080).ToList();
            
            if (previewFormats.Count == 0)
            {
                _logger?.Warn(preferPortraitFormats ? "StartPreviewAsync: no formats <= 1080x1920" : "StartPreviewAsync: no formats <= 1920x1080");
                previewFormats = preferredFormats;
            }
            
            // Ordenar: sin comprimir primero, luego por resolución, luego preferir 9:16 (portrait) o 16:9 (landscape)
            var preferredFormat = previewFormats
                .OrderBy(f => IsUncompressedFormat(f) ? 0 : 1)
                .ThenByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
                .ThenBy(f => preferPortraitFormats ? (Is9_16(f) ? 0 : 1) : (Is16_9(f) ? 0 : 1))
                .ThenBy(f => preferPortraitFormats ? Math.Abs(AspectRatio(f) - AR_9_16) : Math.Abs(AspectRatio(f) - 16.0 / 9.0))
                .FirstOrDefault() ?? frameSource.CurrentFormat;
            
            // Store maximum resolution format for capture
            var maxCaptureFormat = preferredFormats
                .Where(IsUncompressedFormat)
                .OrderByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
                .FirstOrDefault();
            
            // Store formats for later use
            lock (_lock)
            {
                _previewFormat = preferredFormat;
                _maxCaptureFormat = maxCaptureFormat;
                _currentFrameSource = bestFrameSource;
            }
            
            var totalPixels = preferredFormat.VideoFormat.Width * preferredFormat.VideoFormat.Height;
            _logger?.Info($"StartPreviewAsync: selected PREVIEW format {preferredFormat.VideoFormat.Width}x{preferredFormat.VideoFormat.Height} ({totalPixels:N0} pixels)");
            if (maxCaptureFormat != null)
            {
                var maxPixels = maxCaptureFormat.VideoFormat.Width * maxCaptureFormat.VideoFormat.Height;
                _logger?.Info($"StartPreviewAsync: maximum CAPTURE format available: {maxCaptureFormat.VideoFormat.Width}x{maxCaptureFormat.VideoFormat.Height} ({maxPixels:N0} pixels)");
            }

            if (canSetFormat)
            {
                try
                {
                    await frameSource.SetFormatAsync(preferredFormat).AsTask().ConfigureAwait(false);
                    _logger?.Info($"StartPreviewAsync: set format {preferredFormat.VideoFormat.Width}x{preferredFormat.VideoFormat.Height}");
                }
                catch (Exception exFormat)
                {
                    _logger?.Warn($"StartPreviewAsync: SetFormatAsync failed ({exFormat.Message}), using current format");
                    preferredFormat = frameSource.CurrentFormat;
                }
            }
            else
            {
                preferredFormat = frameSource.CurrentFormat;
                _logger?.Info($"StartPreviewAsync: using current format (SharedReadOnly) {preferredFormat.VideoFormat.Width}x{preferredFormat.VideoFormat.Height}");
            }

            reader = await capture.CreateFrameReaderAsync(bestFrameSource).AsTask().ConfigureAwait(false);
            reader.FrameArrived += OnFrameArrived;
            var result = await reader.StartAsync().AsTask().ConfigureAwait(false);
            
            if (result != MediaFrameReaderStartStatus.Success)
            {
                _logger?.Warn($"StartPreviewAsync: frame reader start failed with status {result}");
                reader.Dispose();
                capture.Dispose();
                RaiseCameraError("No se pudo iniciar la lectura de frames.");
                return;
            }

            // Wait a bit for frames to start arriving (some cameras need initialization time)
            await Task.Delay(100).ConfigureAwait(false);
            
            lock (_lock)
            {
                _mediaCapture = capture;
                _frameReader = reader;
                _currentDevice = device;
                _lastOnFrame = onFrame;
                _previewCts = cts;
                _previewTask = Task.CompletedTask; // Frame processing happens in event handler
            }
            _frameCount = 0;
            _lastFrameLogTime = DateTime.MinValue;
            _logger?.Info($"StartPreviewAsync: Active camera = {device.DisplayName} (StableKey={device.StableKey}, DeviceId ok)");

            await Task.CompletedTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error($"StartPreviewAsync: camera \"{device.DisplayName}\" ({device.StableKey}) exception", ex);
            reader?.Dispose();
            capture?.Dispose();
            RaiseCameraError($"Error al iniciar cámara: {ex.Message}");
        }
    }

    private int _frameCount = 0;
    private DateTime _lastFrameLogTime = DateTime.MinValue;

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (_disposed || _previewCts?.Token.IsCancellationRequested == true)
            return;

        try
        {
            var frame = sender.TryAcquireLatestFrame();
            if (frame == null)
                return;
            
            // Log frame arrival every 5 seconds for debugging
            _frameCount++;
            var now = DateTime.Now;
            if ((now - _lastFrameLogTime).TotalSeconds >= 5)
            {
                var activeStableKey = _currentDevice?.StableKey ?? "?";
                _logger?.Info($"Frames arriving from {activeStableKey} (frame #{_frameCount})");
                _lastFrameLogTime = now;
            }

            using (frame)
            {
                var videoFrame = frame.VideoMediaFrame;
                if (videoFrame == null)
                    return;

                var softwareBitmap = videoFrame.SoftwareBitmap;
                if (softwareBitmap == null)
                {
                    // Try Direct3DSurface if SoftwareBitmap is null (some cameras use D3D)
                    var d3dSurface = videoFrame.Direct3DSurface;
                    if (d3dSurface != null)
                    {
                        _logger?.Warn("OnFrameArrived: received Direct3DSurface, SoftwareBitmap conversion needed");
                        // For now, skip D3D frames - they need special handling
                        return;
                    }
                    return;
                }

                // Convert SoftwareBitmap to BGR byte array
                var width = softwareBitmap.PixelWidth;
                var height = softwareBitmap.PixelHeight;
                
                if (width <= 0 || height <= 0)
                    return;
                
                // Ensure BGRA8 format - convert from any format
                SoftwareBitmap? convertedBitmap = null;
                SoftwareBitmap? bitmapToUse = softwareBitmap;
                byte[]? bgrData = null;
                
                try
                {
                    if (softwareBitmap.BitmapPixelFormat != Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8)
                    {
                        convertedBitmap = SoftwareBitmap.Convert(softwareBitmap, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8);
                        bitmapToUse = convertedBitmap;
                    }

                    var pixelData = new byte[width * height * 4];
                    bitmapToUse.CopyToBuffer(pixelData.AsBuffer());

                    // Convert BGRA to BGR
                    bgrData = new byte[width * height * 3];
                    for (int i = 0; i < width * height; i++)
                    {
                        bgrData[i * 3] = pixelData[i * 4];         // B
                        bgrData[i * 3 + 1] = pixelData[i * 4 + 1]; // G
                        bgrData[i * 3 + 2] = pixelData[i * 4 + 2]; // R
                    }

                }
                catch (Exception exConvert)
                {
                    _logger?.Warn($"OnFrameArrived: bitmap conversion exception: {exConvert.Message}");
                    convertedBitmap?.Dispose();
                    return;
                }
                
                if (bgrData == null)
                    return;

                // Forzar 9:16: si la cámara da 16:9 (o cualquier otra relación), recortar al centro a 9:16
                if (_force9_16)
                {
                    const double AR_9_16 = 9.0 / 16.0;
                    double ar = (double)width / height;
                    if (Math.Abs(ar - AR_9_16) > 0.08)
                    {
                        CropBgrTo9_16(bgrData, width, height, out bgrData, out width, out height);
                    }
                }

                // Update snapshot buffer (thread-safe)
                lock (_snapshotLock)
                {
                    _latestSnapshot = bgrData;
                    _latestWidth = width;
                    _latestHeight = height;
                }

                // Call frame callback on background thread (clone data to avoid disposal issues)
                var callback = _lastOnFrame;
                if (callback != null && _previewCts?.Token.IsCancellationRequested != true)
                {
                    var clonedData = new byte[bgrData.Length];
                    Array.Copy(bgrData, clonedData, bgrData.Length);
                    Task.Run(() =>
                    {
                        try
                        {
                            callback(clonedData, width, height);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn($"OnFrameArrived: callback exception: {ex.Message}");
                        }
                    }, _previewCts?.Token ?? CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"OnFrameArrived: exception: {ex.Message}");
        }
    }

    /// <summary>Recorta el frame BGR al centro con relación 9:16. Si ya es 9:16 no hace nada.</summary>
    private static void CropBgrTo9_16(byte[] bgrIn, int w, int h, out byte[] bgrOut, out int outW, out int outH)
    {
        const double AR_9_16 = 9.0 / 16.0;
        double ar = (double)w / h;
        int startX = 0, startY = 0;
        if (ar > AR_9_16)
        {
            outW = (int)Math.Round(h * AR_9_16);
            outH = h;
            startX = (w - outW) / 2;
        }
        else
        {
            outW = w;
            outH = (int)Math.Round(w / AR_9_16);
            startY = (h - outH) / 2;
        }
        bgrOut = new byte[outW * outH * 3];
        int inStride = w * 3;
        int outStride = outW * 3;
        for (int y = 0; y < outH; y++)
        {
            int srcOffset = ((startY + y) * inStride) + (startX * 3);
            int dstOffset = y * outStride;
            System.Buffer.BlockCopy(bgrIn, srcOffset, bgrOut, dstOffset, outStride);
        }
    }

    private const int StopPreviewTimeoutMs = 500;

    public async Task StopPreviewAsync()
    {
        Task? toWait = null;
        MediaFrameReader? readerToStop = null;
        MediaCapture? captureToDispose = null;
        lock (_lock)
        {
            if (_previewCts != null)
            {
                try { _previewCts.Cancel(); } catch { /* ignore */ }
            }
            toWait = _previewTask;
            readerToStop = _frameReader;
            captureToDispose = _mediaCapture;
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
        
        // Stop frame reader first
        if (readerToStop != null)
        {
            try
            {
                _logger?.Info("StopPreviewAsync: stopping frame reader");
                readerToStop.FrameArrived -= OnFrameArrived;
                await readerToStop.StopAsync().AsTask().ConfigureAwait(false);
                readerToStop.Dispose();
            }
            catch (Exception ex) { _logger?.Warn($"StopPreviewAsync: frame reader stop exception: {ex.Message}"); }
        }
        
        // Wait for any pending operations (with timeout)
        if (toWait != null)
        {
            try
            {
                var completed = await Task.WhenAny(toWait, Task.Delay(StopPreviewTimeoutMs)).ConfigureAwait(false);
                if (completed != toWait)
                    _logger?.Info($"StopPreviewAsync: previous operations did not complete in {StopPreviewTimeoutMs} ms, continuing anyway");
                else
                    _logger?.Info("StopPreviewAsync: previous operations completed cleanly");
            }
            catch (OperationCanceledException) { _logger?.Info("StopPreviewAsync: operations cancelled"); }
            catch (Exception ex) { _logger?.Info($"StopPreviewAsync: wait ended: {ex.Message}"); }
        }
        
        // Dispose MediaCapture AFTER reader stops
        if (captureToDispose != null)
        {
            try
            {
                _logger?.Info("StopPreviewAsync: disposing MediaCapture");
                captureToDispose.Dispose();
            }
            catch (Exception ex) { _logger?.Warn($"StopPreviewAsync: MediaCapture dispose exception: {ex.Message}"); }
        }
        
        // Clear snapshot
        lock (_snapshotLock)
        {
            _latestSnapshot = null;
            _latestWidth = 0;
            _latestHeight = 0;
        }
        
        _logger?.Info("StopPreviewAsync: stopped");
    }

    public Task<CaptureResult?> CaptureStillAsync(CancellationToken cancellationToken = default)
    {
        // Use preview snapshot - this avoids breaking preview state and allows camera switching to work normally
        // The preview is already running at reasonable resolution (max 1920x1080) which is good quality
        byte[]? snapshot;
        int width, height;
        lock (_snapshotLock)
        {
            if (_latestSnapshot == null)
            {
                _logger?.Warn("CaptureStillAsync: no snapshot available");
                return Task.FromResult<CaptureResult?>(null);
            }
            snapshot = new byte[_latestSnapshot.Length];
            Array.Copy(_latestSnapshot, snapshot, _latestSnapshot.Length);
            width = _latestWidth;
            height = _latestHeight;
        }
        _logger?.Info($"CaptureStillAsync: returning preview snapshot {width}x{height}");
        return Task.FromResult<CaptureResult?>(new CaptureResult { Bgr = snapshot, Width = width, Height = height });
    }


    private bool IsGenericName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true;
            
        var lower = name.ToLowerInvariant();
        
        // Check for generic patterns
        if (lower == "camera" || lower == "cámara" || lower == "unknown camera" || lower == "cámara desconocida")
            return true;
            
        // Check for numbered generic names (e.g., "Camera 0", "Cámara 0", "Camera 1")
        if (Regex.IsMatch(lower, @"^(camera|cámara)\s*\d+$"))
            return true;
            
        // Check for generic USB video device names
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
        _disposed = true;
        StopPreviewAsync().GetAwaiter().GetResult();
    }
}
