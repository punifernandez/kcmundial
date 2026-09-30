using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;
using KCMundial.Camera;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;
using KCMundial.Processing;
using KCMundial.Vision;

namespace KCMundial.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private INavigationService? _navigation;
    private readonly ICameraManager _cameraManager;
    private readonly IFaceDetector _faceDetector;
    private readonly IPositioningValidator _positioningValidator;
    private readonly ExportService _exportService;
    private readonly LocalServerHost _serverHost;
    private readonly IPathResolver _pathResolver;
    private readonly IAppLogger? _logger;
    private const string CameraPreferenceFileName = "kcmundial_camera.txt";
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _previewCts;
    private int _frameCount;
    private const int FaceDetectionEveryNFrames = 5;

    [ObservableProperty]
    private WriteableBitmap? _previewImage;

    [ObservableProperty]
    private string _guidanceMessage = "No te veo la cara";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    private bool _isPositionOk;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    private bool _isCaptureEnabled = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CaptureCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private ObservableCollection<CameraDevice> _cameras = new();

    [ObservableProperty]
    private CameraDevice? _selectedCamera;

    [ObservableProperty]
    private bool _isCountdownVisible;

    [ObservableProperty]
    private int _countdownNumber;

    [ObservableProperty]
    private bool _isProcessingVisible;

    [ObservableProperty]
    private string? _cameraError;

    /// <summary>Updated only when preview is really running (after StartPreviewAsync succeeds). For "Activa: ..." label.</summary>
    [ObservableProperty]
    private string _activeCameraDisplayName = "";

    /// <summary>Marco seleccionado (1, 2 o 3). Al cambiar se actualiza el overlay del preview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewBackOverlayImage))]
    [NotifyPropertyChangedFor(nameof(IsFrame1Selected))]
    [NotifyPropertyChangedFor(nameof(IsFrame2Selected))]
    [NotifyPropertyChangedFor(nameof(IsFrame3Selected))]
    private int _selectedFrameIndex = 1;

    /// <summary>Overlay del marco en el preview (cambia con SelectedFrameIndex).</summary>
    public ImageSource? PreviewBackOverlayImage => _previewBackOverlay ??= LoadPreviewBackOverlay();
    private ImageSource? _previewBackOverlay;

    /// <summary>Miniaturas de los 3 marcos para el selector (Fondo_1, Fondo_2, Fondo_3).</summary>
    public ImageSource? FrameThumbnail1 => _frameThumb1 ??= LoadFrameThumbnail(1);
    public ImageSource? FrameThumbnail2 => _frameThumb2 ??= LoadFrameThumbnail(2);
    public ImageSource? FrameThumbnail3 => _frameThumb3 ??= LoadFrameThumbnail(3);
    private ImageSource? _frameThumb1;
    private ImageSource? _frameThumb2;
    private ImageSource? _frameThumb3;

    public bool IsFrame1Selected => SelectedFrameIndex == 1;
    public bool IsFrame2Selected => SelectedFrameIndex == 2;
    public bool IsFrame3Selected => SelectedFrameIndex == 3;

    private int _previewWidth = 720;
    private int _previewHeight = 1280;
    private int _previewGeneration;
    private bool _isInitializing;
    private CameraDevice? _currentDevice;
    private readonly SemaphoreSlim _cameraSwitchLock = new(1, 1);
    private DateTime _lastDebugSave = DateTime.MinValue;
    private const int DebugSaveIntervalSeconds = 5;
    private static readonly string SoundsFolder = Path.Combine(AppContext.BaseDirectory, "assets", "sounds");
    private readonly MediaPlayer _soundPlayer = new MediaPlayer();

    public event Action? InitializationComplete;

    public MainViewModel(
        ICameraManager cameraManager,
        IFaceDetector faceDetector,
        IPositioningValidator positioningValidator,
        ExportService exportService,
        LocalServerHost serverHost,
        IPathResolver pathResolver,
        IAppLogger? logger)
    {
        _cameraManager = cameraManager;
        _faceDetector = faceDetector;
        _positioningValidator = positioningValidator;
        _exportService = exportService;
        _serverHost = serverHost;
        _pathResolver = pathResolver;
        _logger = logger;
        _cameraManager.CameraError += OnCameraError;
    }

    public void SetNavigation(INavigationService navigation) => _navigation = navigation;

    partial void OnSelectedFrameIndexChanged(int value)
    {
        _previewBackOverlay = null;
        OnPropertyChanged(nameof(PreviewBackOverlayImage));
    }

    private ImageSource? LoadPreviewBackOverlay()
    {
        var path = _pathResolver.GetFramePath(SelectedFrameIndex);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private ImageSource? LoadFrameThumbnail(int frameIndex)
    {
        var path = _pathResolver.GetFramePath(frameIndex);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 120;
            bi.UriSource = new Uri(path, UriKind.Absolute);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private void SelectFrame(object? parameter)
    {
        if (parameter is int i)
        {
            var n = Math.Clamp(i, 1, 3);
            if (SelectedFrameIndex != n) SelectedFrameIndex = n;
            return;
        }
        if (parameter is string s && int.TryParse(s, out var parsed))
        {
            var n = Math.Clamp(parsed, 1, 3);
            if (SelectedFrameIndex != n) SelectedFrameIndex = n;
        }
    }

    partial void OnSelectedCameraChanged(CameraDevice? value)
    {
        if (value == null || _isInitializing)
        {
            _logger?.Info($"MainViewModel: OnSelectedCameraChanged ignored (value={value?.DisplayName}, _isInitializing={_isInitializing})");
            return;
        }
    }

    public void UserSelectedCamera(CameraDevice device)
    {
        if (_isInitializing)
        {
            _logger?.Info($"MainViewModel: UserSelectedCamera ignored during init (device=\"{device.DisplayName}\")");
            return;
        }
        if (_currentDevice?.DeviceId == device.DeviceId)
        {
            _logger?.Info($"MainViewModel: UserSelectedCamera ignored, already on camera \"{device.DisplayName}\"");
            return;
        }
        _logger?.Info($"MainViewModel: Requested camera = {device.DisplayName} (StableKey={device.StableKey}, DeviceId)");
        
        // Update UI immediately
        _dispatcher.Invoke(() =>
        {
            CameraError = null;
            IsCaptureEnabled = false;
            PreviewImage = null;
            GuidanceMessage = "Conectando...";
        });
        
        // Start camera switch in background - rely on CameraManager for orderly switching
        _ = Task.Run(async () =>
        {
            await _cameraSwitchLock.WaitAsync();
            try
            {
                await StartPreviewAsync(device).ConfigureAwait(false);
                _currentDevice = device;
                SaveCameraPreference(device.DeviceId);
                _dispatcher.Invoke(() =>
                {
                    var matchingCamera = Cameras.FirstOrDefault(c => c.DeviceId == device.DeviceId);
                    if (matchingCamera != null)
                        SelectedCamera = matchingCamera;
                });
                _logger?.Info($"MainViewModel: Active camera = {device.DisplayName} (preview started)");
            }
            catch (Exception ex)
            {
                _logger?.Error($"MainViewModel: StartPreview failed for \"{device.DisplayName}\"", ex);
                _dispatcher.Invoke(() =>
                {
                    ActiveCameraDisplayName = "";
                    CameraError = $"Error: {ex.Message}";
                    GuidanceMessage = "Error al conectar";
                    if (_currentDevice != null)
                        SelectedCamera = Cameras.FirstOrDefault(c => c.DeviceId == _currentDevice.DeviceId) ?? _currentDevice;
                });
            }
            finally
            {
                _cameraSwitchLock.Release();
            }
        });
    }

    private void OnCameraError(object? sender, string message)
    {
        _dispatcher.Invoke(() =>
        {
            CameraError = message;
            IsCaptureEnabled = false;
        });
    }

    public async Task InitializeAsync()
    {
        _logger?.Info("MainViewModel: enumerating cameras (Windows.Devices.Enumeration)");
        _isInitializing = true;
        IReadOnlyList<CameraDevice> list;
        try
        {
            list = await _cameraManager.GetCamerasAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Error("MainViewModel: GetCamerasAsync failed", ex);
            list = Array.Empty<CameraDevice>();
        }
        _logger?.Info($"MainViewModel: enumerated {list.Count} camera(s)");
        foreach (var c in list)
        {
            var idPreview = c.Id.Length > 8 ? c.Id.Substring(0, 8) + "..." : c.Id;
            _logger?.Info($"  name=\"{c.Name}\" id={idPreview}");
        }
        await _dispatcher.InvokeAsync(() =>
        {
            Cameras.Clear();
            foreach (var c in list)
                Cameras.Add(c);
        });
        await Task.Delay(200);
        var preferredDeviceId = LoadCameraPreference();
        CameraDevice? toSelect = null;
        if (!string.IsNullOrWhiteSpace(preferredDeviceId))
        {
            toSelect = list.FirstOrDefault(c => string.Equals(c.DeviceId, preferredDeviceId.Trim(), StringComparison.Ordinal));
            if (toSelect != null)
                _logger?.Info($"MainViewModel: restored preference by DeviceId: {toSelect.DisplayName} (StableKey={toSelect.StableKey})");
            else
            {
                _logger?.Info($"MainViewModel: preferred DeviceId not found in list");
                // Si la preferencia no coincide (ej. Brio en otro puerto USB), intentar misma cámara por nombre (ej. "BRIO")
                var byName = list.FirstOrDefault(c => c.DisplayName.Contains("BRIO", StringComparison.OrdinalIgnoreCase));
                if (byName != null)
                {
                    toSelect = byName;
                    _logger?.Info($"MainViewModel: fallback to camera by name: {toSelect.DisplayName}");
                }
            }
        }
        if (toSelect == null && list.Count > 0)
            toSelect = list[0];
        await _dispatcher.InvokeAsync(() =>
        {
            SelectedCamera = toSelect ?? (list.Count > 0 ? list[0] : null);
        });
        await Task.Delay(300);
        _isInitializing = false;
        InitializationComplete?.Invoke();
        if (toSelect != null)
            _logger?.Info($"MainViewModel: starting preview for camera {toSelect.DisplayName} (StableKey={toSelect.StableKey})");
        else
        {
            _logger?.Warn("MainViewModel: no camera to start preview");
        }
        await _cameraSwitchLock.WaitAsync();
        try
        {
        if (toSelect != null)
        {
            await StartPreviewAsync(toSelect).ConfigureAwait(false);
            _currentDevice = toSelect;
            SaveCameraPreference(toSelect.DeviceId);
            await _dispatcher.InvokeAsync(() =>
            {
                if (SelectedCamera?.DeviceId != toSelect.DeviceId)
                    SelectedCamera = toSelect;
            });
        }
        }
        catch (Exception ex)
        {
            _logger?.Error($"MainViewModel: StartPreview failed for \"{toSelect?.DisplayName ?? "(none)"}\"", ex);
            _dispatcher.Invoke(() => CameraError = $"Error: {ex.Message}");
        }
        finally
        {
            _cameraSwitchLock.Release();
        }
    }
    private string GetCameraPreferencePath() => Path.Combine(_pathResolver.RootInstallPath, CameraPreferenceFileName);
    private string? LoadCameraPreference()
    {
        try
        {
            var path = GetCameraPreferencePath();
            if (File.Exists(path))
            {
                var deviceId = File.ReadAllText(path).Trim();
                return string.IsNullOrEmpty(deviceId) ? null : deviceId;
            }
        }
        catch (Exception ex)
        {
            _logger?.Warn($"LoadCameraPreference: {ex.Message}");
        }
        return null;
    }
    private void SaveCameraPreference(string preferredCameraDeviceId)
    {
        try
        {
            var path = GetCameraPreferencePath();
            File.WriteAllText(path, preferredCameraDeviceId ?? "");
        }
        catch (Exception ex)
        {
            _logger?.Warn($"SaveCameraPreference: {ex.Message}");
        }
    }

    public async Task StartPreviewAsync(CameraDevice device)
    {
        _logger?.Info($"StartPreviewAsync: Requested camera = {device.DisplayName} (StableKey={device.StableKey})");
        _dispatcher.Invoke(() => ActiveCameraDisplayName = "");
        var gen = ++_previewGeneration;
        _previewCts = new CancellationTokenSource();
        _positioningValidator.SetFrameSize(_previewWidth, _previewHeight);
        Action<byte[], int, int> callback = (bgr, w, h) =>
        {
            if (gen != _previewGeneration) return;
            OnPreviewFrame(bgr, w, h);
        };
        await _cameraManager.StartPreviewAsync(device, callback, _previewCts.Token, preferPortraitFormats: true).ConfigureAwait(false);
        _dispatcher.Invoke(() =>
        {
            ActiveCameraDisplayName = device.DisplayName;
            if (_currentDevice?.DeviceId == device.DeviceId)
                GuidanceMessage = "No te veo la cara";
        });
        _logger?.Info($"StartPreviewAsync: Active camera = {device.DisplayName} (preview running)");
    }

    private void OnPreviewFrame(byte[] bgr, int width, int height)
    {
        _previewWidth = width;
        _previewHeight = height;
        _dispatcher.Invoke(() =>
        {
            UpdatePreviewBitmap(bgr, width, height);
            _frameCount++;
            if (_frameCount % FaceDetectionEveryNFrames == 0)
            {
                var copy = new byte[bgr.Length];
                Array.Copy(bgr, copy, bgr.Length);
                Task.Run(() =>
                {
                    var result = RunPositioningValidationOffThread(copy, width, height);
                    _dispatcher.Invoke(() => ApplyPositioningResult(result));
                });
            }
            if ((DateTime.UtcNow - _lastDebugSave).TotalSeconds >= DebugSaveIntervalSeconds)
            {
                _lastDebugSave = DateTime.UtcNow;
                var debugCopy = new byte[bgr.Length];
                Array.Copy(bgr, debugCopy, bgr.Length);
                var w = width;
                var h = height;
                Task.Run(() => _exportService.SaveDebugFrame(debugCopy, w, h));
            }
        });
    }

    private PositioningResult RunPositioningValidationOffThread(byte[] bgr, int width, int height)
    {
        _positioningValidator.SetFrameSize(width, height);
        var faces = _faceDetector.Detect(bgr, width, height);
        FaceInfo? primary = null;
        if (faces.Count > 0)
        {
            var f = faces[0];
            primary = new FaceInfo
            {
                CenterX = f.CenterX,
                CenterY = f.CenterY,
                Width = f.Width,
                Height = f.Height,
                EyesY = f.Y + f.Height * 0.35
            };
        }
        return _positioningValidator.Validate(faces.Count, primary);
    }

    private void ApplyPositioningResult(PositioningResult result)
    {
        GuidanceMessage = result.GuidanceMessage;
        IsPositionOk = result.IsOk;
        IsCaptureEnabled = true; // Allow capture regardless of face/position
    }

    private void UpdatePreviewBitmap(byte[] bgr, int width, int height)
    {
        if (PreviewImage == null || PreviewImage.PixelWidth != width || PreviewImage.PixelHeight != height)
            PreviewImage = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr24, null);
        PreviewImage!.WritePixels(new Int32Rect(0, 0, width, height), bgr, width * 3, 0);
    }


    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        if (!IsPositionOk || IsBusy) return;
        IsBusy = true;
        IsCaptureEnabled = false;
        try
        {
            for (var i = 3; i >= 1; i--)
            {
                IsCountdownVisible = true;
                CountdownNumber = i;
                PlaySound("ticktack");
                await Task.Delay(1000);
            }
            IsCountdownVisible = false;
            IsProcessingVisible = true;
            
            _logger?.Info("CaptureAsync: starting capture");
            PlaySound("shutter");
            var capture = await _cameraManager.CaptureStillAsync();
            if (capture == null)
            {
                _logger?.Warn("CaptureAsync: capture returned null");
                IsProcessingVisible = false;
                CameraError = "Error al capturar.";
                return;
            }

            _logger?.Info($"CaptureAsync: capture successful, starting export");
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? id = null;
            try
            {
                id = await _exportService.ExportAsync(capture, SelectedFrameIndex, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger?.Error("CaptureAsync: export timed out");
                CameraError = "El procesamiento tardó demasiado.";
            }
            catch (Exception ex)
            {
                _logger?.Error("CaptureAsync: export exception", ex);
                CameraError = $"Error al procesar: {ex.Message}";
            }
            
            IsProcessingVisible = false;
            if (id != null && _navigation != null)
            {
                _logger?.Info($"CaptureAsync: export successful, navigating to result {id}");
                _navigation.NavigateToResult(id);
            }
            else if (id == null)
            {
                _logger?.Warn("CaptureAsync: export returned null");
                CameraError = "Error al procesar.";
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("CaptureAsync: unexpected exception", ex);
            IsProcessingVisible = false;
            CameraError = $"Error inesperado: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            if (IsPositionOk)
                IsCaptureEnabled = true;
        }
    }

    private void PlaySound(string name)
    {
        foreach (var ext in new[] { ".mp3", ".wav" })
        {
            var path = Path.Combine(SoundsFolder, name + ext);
            if (!File.Exists(path)) continue;
            try
            {
                _soundPlayer.Open(new Uri(path, UriKind.Absolute));
                _soundPlayer.Play();
                return;
            }
            catch (Exception ex)
            {
                _logger?.Info($"PlaySound: could not play {path}: {ex.Message}");
            }
        }
    }

    private bool CanCapture() => !IsBusy;

    public void OnReturnFromResult()
    {
        IsProcessingVisible = false;
        IsCountdownVisible = false;
        
        // Ensure SelectedCamera is synchronized with current device when returning
        _dispatcher.Invoke(() =>
        {
            if (_currentDevice != null)
            {
                var matchingCamera = Cameras.FirstOrDefault(c => c.DeviceId == _currentDevice.DeviceId);
                if (matchingCamera != null)
                    SelectedCamera = matchingCamera;
            }
            if (IsPositionOk)
                IsCaptureEnabled = true;
        });
    }

    [RelayCommand]
    private void OpenGallery()
    {
        _navigation?.NavigateToGallery();
    }

    [RelayCommand]
    private void CloseApp()
    {
        _navigation?.Close();
    }
}
