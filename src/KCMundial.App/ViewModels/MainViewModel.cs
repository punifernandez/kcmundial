using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;
using KCMundial.Processing;

namespace KCMundial.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private INavigationService? _navigation;
    private readonly ICameraManager _cameraManager;
    private readonly IFaceDetector _faceDetector;
    private readonly IPositioningValidator _positioningValidator;
    private readonly ExportService _exportService;
    private readonly IPathResolver _pathResolver;
    private readonly AppSettings _settings;
    private readonly IAppLogger? _logger;
    private const string CameraPreferenceFileName = "kcmundial_camera.txt";
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // Preview: la cámara escribe el último cuadro acá y la UI lo pinta cuando puede (nunca se encolan cuadros).
    private readonly object _frameLock = new();
    private byte[]? _frameBuffer;
    private int _frameWidth;
    private int _frameHeight;
    private int _renderQueued;

    // Detección de caras: como máximo una a la vez, sobre una versión chica del área visible.
    private const int DetectionIntervalMs = 150;
    private const int DetectionWidth = 480;
    private int _detectionRunning;
    private long _lastDetectionTick;
    private byte[]? _detectionBuffer;

    private readonly Dictionary<string, MediaPlayer> _sounds = new();
    private static readonly string SoundsFolder = Path.Combine(AppContext.BaseDirectory, "assets", "sounds");

    private int _previewGeneration;
    private bool _isInitializing;
    private CameraDevice? _currentDevice;
    private readonly SemaphoreSlim _cameraSwitchLock = new(1, 1);

    [ObservableProperty]
    private WriteableBitmap? _previewImage;

    [ObservableProperty]
    private string _guidanceMessage = "";

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

    [ObservableProperty]
    private bool _isCameraReady;

    [ObservableProperty]
    private string _activeCameraDisplayName = "";

    [ObservableProperty]
    private bool _isAdminPanelOpen;

    /// <summary>Marco seleccionado (1, 2 o 3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFrame1Selected))]
    [NotifyPropertyChangedFor(nameof(IsFrame2Selected))]
    [NotifyPropertyChangedFor(nameof(IsFrame3Selected))]
    private int _selectedFrameIndex = 1;

    [ObservableProperty]
    private ImageSource? _previewFrameOverlay;

    /// <summary>Tamaño lógico del área de preview: misma proporción que el marco (se escala con un Viewbox).</summary>
    [ObservableProperty]
    private double _previewBoxWidth = 1000;

    [ObservableProperty]
    private double _previewBoxHeight = 1500;

    public ImageSource? FrameThumbnail1 { get; }
    public ImageSource? FrameThumbnail2 { get; }
    public ImageSource? FrameThumbnail3 { get; }

    public bool IsFrame1Selected => SelectedFrameIndex == 1;
    public bool IsFrame2Selected => SelectedFrameIndex == 2;
    public bool IsFrame3Selected => SelectedFrameIndex == 3;

    public double PreviewRotation => FiguritaComposer.NormalizeRotation(_settings.CameraRotation);
    public double PreviewMirrorScale => _settings.MirrorPreview ? -1 : 1;

    /// <summary>Pide a la vista el destello blanco del disparo.</summary>
    public event Action? FlashRequested;
    public event Action? InitializationComplete;

    public MainViewModel(
        ICameraManager cameraManager,
        IFaceDetector faceDetector,
        IPositioningValidator positioningValidator,
        ExportService exportService,
        IPathResolver pathResolver,
        AppSettings settings,
        IAppLogger? logger)
    {
        _cameraManager = cameraManager;
        _faceDetector = faceDetector;
        _positioningValidator = positioningValidator;
        _exportService = exportService;
        _pathResolver = pathResolver;
        _settings = settings;
        _logger = logger;
        _cameraManager.CameraError += OnCameraError;

        FrameThumbnail1 = QrImageFactory.LoadImage(_pathResolver.GetFramePath(1), 240);
        FrameThumbnail2 = QrImageFactory.LoadImage(_pathResolver.GetFramePath(2), 240);
        FrameThumbnail3 = QrImageFactory.LoadImage(_pathResolver.GetFramePath(3), 240);
        LoadFrameOverlay();
        PreloadSounds();
    }

    public void SetNavigation(INavigationService navigation) => _navigation = navigation;

    // ---------------------------------------------------------------- Marcos

    partial void OnSelectedFrameIndexChanged(int value) => LoadFrameOverlay();

    private void LoadFrameOverlay()
    {
        var overlay = QrImageFactory.LoadImage(_pathResolver.GetFramePath(SelectedFrameIndex), 1200);
        PreviewFrameOverlay = overlay;
        var aspect = overlay is BitmapSource b && b.PixelHeight > 0 ? (double)b.PixelWidth / b.PixelHeight : FiguritaComposer.DefaultAspect;
        PreviewBoxWidth = 1000 * aspect;
        PreviewBoxHeight = 1000;
    }

    private double FrameAspect => PreviewBoxWidth / PreviewBoxHeight;

    [RelayCommand]
    private void SelectFrame(object? parameter)
    {
        if (IsBusy) return;
        var n = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => SelectedFrameIndex
        };
        SelectedFrameIndex = Math.Clamp(n, 1, 3);
    }

    // ---------------------------------------------------------------- Cámara

    public void UserSelectedCamera(CameraDevice device)
    {
        if (_isInitializing || _currentDevice?.DeviceId == device.DeviceId)
            return;
        _logger?.Info($"MainViewModel: user selected camera {device.DisplayName}");
        CameraError = null;
        IsCameraReady = false;
        PreviewImage = null;

        _ = Task.Run(async () =>
        {
            await _cameraSwitchLock.WaitAsync();
            try
            {
                await StartPreviewAsync(device).ConfigureAwait(false);
                _currentDevice = device;
                SaveCameraPreference(device.DeviceId);
            }
            catch (Exception ex)
            {
                _logger?.Error($"MainViewModel: StartPreview failed for \"{device.DisplayName}\"", ex);
                _dispatcher.Invoke(() => CameraError = $"No se pudo abrir la cámara: {ex.Message}");
            }
            finally
            {
                _cameraSwitchLock.Release();
            }
        });
    }

    private void OnCameraError(object? sender, string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            CameraError = message;
            IsCameraReady = false;
        });
    }

    public async Task InitializeAsync()
    {
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
        _logger?.Info($"MainViewModel: {list.Count} camera(s): {string.Join(", ", list.Select(c => c.DisplayName))}");

        var preferredId = LoadCameraPreference();
        var toSelect = list.FirstOrDefault(c => string.Equals(c.DeviceId, preferredId, StringComparison.Ordinal))
                       // La Brio puede cambiar de Id si se enchufa en otro puerto: buscarla por nombre.
                       ?? list.FirstOrDefault(c => c.DisplayName.Contains("BRIO", StringComparison.OrdinalIgnoreCase))
                       ?? list.FirstOrDefault();

        await _dispatcher.InvokeAsync(() =>
        {
            Cameras.Clear();
            foreach (var c in list) Cameras.Add(c);
            SelectedCamera = toSelect;
            if (toSelect == null) CameraError = "No se encontró ninguna cámara.";
        });
        _isInitializing = false;
        InitializationComplete?.Invoke();
        if (toSelect == null) return;

        await _cameraSwitchLock.WaitAsync();
        try
        {
            await StartPreviewAsync(toSelect).ConfigureAwait(false);
            _currentDevice = toSelect;
            SaveCameraPreference(toSelect.DeviceId);
        }
        catch (Exception ex)
        {
            _logger?.Error($"MainViewModel: StartPreview failed for \"{toSelect.DisplayName}\"", ex);
            _dispatcher.Invoke(() => CameraError = $"No se pudo abrir la cámara: {ex.Message}");
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
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"LoadCameraPreference: {ex.Message}");
            return null;
        }
    }

    private void SaveCameraPreference(string deviceId)
    {
        try { File.WriteAllText(GetCameraPreferencePath(), deviceId ?? ""); }
        catch (Exception ex) { _logger?.Warn($"SaveCameraPreference: {ex.Message}"); }
    }

    private async Task StartPreviewAsync(CameraDevice device)
    {
        var gen = Interlocked.Increment(ref _previewGeneration);
        await _cameraManager.StartPreviewAsync(device, (bgra, w, h) =>
        {
            if (gen == Volatile.Read(ref _previewGeneration))
                OnCameraFrame(bgra, w, h);
        }).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            ActiveCameraDisplayName = device.DisplayName;
            SelectedCamera = Cameras.FirstOrDefault(c => c.DeviceId == device.DeviceId) ?? SelectedCamera;
        });
    }

    /// <summary>Hilo de la cámara: copia el cuadro y pide un repintado si no hay uno pendiente.</summary>
    private void OnCameraFrame(byte[] bgra, int width, int height)
    {
        lock (_frameLock)
        {
            if (_frameBuffer == null || _frameBuffer.Length != bgra.Length)
                _frameBuffer = new byte[bgra.Length];
            Buffer.BlockCopy(bgra, 0, _frameBuffer, 0, bgra.Length);
            _frameWidth = width;
            _frameHeight = height;
        }
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            _dispatcher.BeginInvoke(RenderLatestFrame, DispatcherPriority.Render);

        MaybeStartFaceDetection(bgra, width, height);
    }

    private void RenderLatestFrame()
    {
        Volatile.Write(ref _renderQueued, 0);
        lock (_frameLock)
        {
            if (_frameBuffer == null) return;
            if (PreviewImage == null || PreviewImage.PixelWidth != _frameWidth || PreviewImage.PixelHeight != _frameHeight)
                PreviewImage = new WriteableBitmap(_frameWidth, _frameHeight, 96, 96, PixelFormats.Bgr32, null);
            PreviewImage.WritePixels(new Int32Rect(0, 0, _frameWidth, _frameHeight), _frameBuffer, _frameWidth * 4, 0);
        }
        if (!IsCameraReady)
        {
            IsCameraReady = true;
            CameraError = null;
        }
    }

    private void MaybeStartFaceDetection(byte[] bgra, int width, int height)
    {
        var now = Environment.TickCount64;
        if (IsBusy || now - Interlocked.Read(ref _lastDetectionTick) < DetectionIntervalMs) return;
        if (Interlocked.CompareExchange(ref _detectionRunning, 1, 0) != 0) return;

        FrameSampler.SampleVisibleBgr(bgra, width, height, _settings.CameraRotation, FrameAspect, DetectionWidth,
            ref _detectionBuffer, out var w, out var h);
        var buffer = _detectionBuffer!;
        Task.Run(() =>
        {
            try
            {
                var faces = _faceDetector.Detect(buffer, w, h);
                FaceInfo? primary = null;
                if (faces.Count > 0)
                {
                    var f = faces.OrderByDescending(x => x.Width * x.Height).First();
                    primary = new FaceInfo { CenterX = f.CenterX, CenterY = f.CenterY, Width = f.Width, Height = f.Height, EyesY = f.Y + f.Height * 0.35 };
                }
                _positioningValidator.SetFrameSize(w, h);
                var result = _positioningValidator.Validate(faces.Count, primary);
                _dispatcher.BeginInvoke(() => GuidanceMessage = result.GuidanceMessage);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"Face detection: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _lastDetectionTick, Environment.TickCount64);
                Volatile.Write(ref _detectionRunning, 0);
            }
        });
    }

    // ---------------------------------------------------------------- Captura

    private bool CanCapture() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCapture))]
    private async Task CaptureAsync()
    {
        IsBusy = true;
        IsAdminPanelOpen = false;
        CameraError = null;
        GuidanceMessage = "";
        try
        {
            IsCountdownVisible = true;
            for (var i = _settings.CountdownSeconds; i >= 1; i--)
            {
                CountdownNumber = i;
                PlaySound("ticktack");
                await Task.Delay(1000);
            }
            IsCountdownVisible = false;

            PlaySound("shutter");
            FlashRequested?.Invoke();
            // El cartel tapa el instante en que la cámara cambia a 4K y el preview se congela.
            IsProcessingVisible = true;
            var capture = await _cameraManager.CaptureStillAsync(_settings.HighResCapture);
            if (capture == null)
            {
                CameraError = "No se pudo sacar la foto. Probá de nuevo.";
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await _exportService.ExportAsync(capture, SelectedFrameIndex, _settings.CameraRotation, cts.Token);
            if (result == null)
            {
                CameraError = "No se pudo armar la foto. Probá de nuevo.";
                return;
            }
            _navigation?.NavigateToResult(result);
        }
        catch (OperationCanceledException)
        {
            _logger?.Error("CaptureAsync: export timed out");
            CameraError = "El procesamiento tardó demasiado. Probá de nuevo.";
        }
        catch (Exception ex)
        {
            _logger?.Error("CaptureAsync: unexpected exception", ex);
            CameraError = $"Error inesperado: {ex.Message}";
        }
        finally
        {
            IsCountdownVisible = false;
            IsProcessingVisible = false;
            IsBusy = false;
        }
    }

    private void PreloadSounds()
    {
        foreach (var name in new[] { "ticktack", "shutter" })
        {
            var path = new[] { ".wav", ".mp3" }.Select(ext => Path.Combine(SoundsFolder, name + ext)).FirstOrDefault(File.Exists);
            if (path == null) continue;
            try
            {
                var player = new MediaPlayer();
                player.Open(new Uri(path, UriKind.Absolute));
                _sounds[name] = player;
            }
            catch (Exception ex)
            {
                _logger?.Info($"Sound {name}: {ex.Message}");
            }
        }
    }

    private void PlaySound(string name)
    {
        if (!_sounds.TryGetValue(name, out var player)) return;
        player.Stop();
        player.Position = TimeSpan.Zero;
        player.Play();
    }

    public void OnReturnFromResult()
    {
        IsProcessingVisible = false;
        IsCountdownVisible = false;
        IsAdminPanelOpen = false;
    }

    // ---------------------------------------------------------------- Operador

    [RelayCommand]
    private void ToggleAdminPanel() => IsAdminPanelOpen = !IsAdminPanelOpen;

    [RelayCommand]
    private void OpenGallery()
    {
        IsAdminPanelOpen = false;
        _navigation?.NavigateToGallery();
    }

    [RelayCommand]
    private void CloseApp()
    {
        IsAdminPanelOpen = false;
        _navigation?.Close();
    }
}
