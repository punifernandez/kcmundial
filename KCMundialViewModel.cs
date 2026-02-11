using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Media;
using System.Printing;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.Media.Capture;
using KCMundial.Services;

namespace KCMundial
{
    public enum KCMundialState
    {
        Idle,
        Previewing,
        Countdown,
        Capturing,
        Composing,
        Result,
        Error
    }

    public enum PrintDesignType
    {
        Strip3Photos,      // 3 fotos vertical (actual)
        InstantPhoto,    // 1 foto instantánea
        TwoPhotosHorizontal // 2 fotos horizontal con leyenda
    }

    public class KCMundialViewModel : INotifyPropertyChanged
    {
        private KCMundialState _state = KCMundialState.Idle;
        private string _countdownText = "";
        private string _errorMessage = "";
        private string _resultStripPath = "";
        private System.Windows.Media.Imaging.BitmapImage? _resultStripImage;
        private bool _canStart = false;
        private ObservableCollection<CameraInfo> _availableCameras = new();
        private CameraInfo? _selectedCamera;
        private List<FrameInfo> _availableFrames = new();
        private FrameInfo? _selectedFrame;
        private List<DesignInfo> _availableDesigns = new();
        private DesignInfo? _selectedDesign;
        private ObservableCollection<string> _availablePrinters = new();
        private string? _selectedPrinter;

        private readonly CameraService _cameraService;
        private readonly CollageService _collageService;
        private readonly StorageService _storageService;
        private readonly Services.PersonSegmentationService _segmentationService;
        private readonly Services.RemoveBgService _removeBgService;
        // Servicios de reemplazo de fondo
        private Services.BackgroundSegmentationService? _backgroundSegmentationService;
        private Services.AlphaMattePostProcessor? _alphaPostProcessor;
        private Services.BackgroundComposer? _backgroundComposer;
        
        // Pipelines separados: Preview (rápido) y Final (calidad)
        private Services.IPipelinePreview? _pipelinePreview;
        private Services.IPipelineFinal? _pipelineFinal;

        private int _currentPhotoIndex = 0;
        private List<string> _capturedPhotos = new();
        private CancellationTokenSource? _cancellationTokenSource;
        
        // Background replacement
        private SkiaSharp.SKBitmap? _backgroundBitmap;
        private BitmapSource? _lastProcessedFrame;
        private readonly object _frameProcessingLock = new object();
        private bool _firstFrameLogged = false;
        
        // Phase4 subfase tracking
        private readonly Phase4SubPhase _phase4SubPhase;
        private bool _phase4Disabled = false; // Circuit breaker
        private int _consecutiveFailures = 0;
        
        // Debounce y cancelación
        private DateTime _lastProcessedTime = DateTime.MinValue;
        private readonly TimeSpan _debounceInterval = TimeSpan.FromMilliseconds(200);
        private Task? _currentProcessingTask = null;
        private CancellationTokenSource? _currentProcessingCts = null;
        
        // Pipeline Final: control de ejecución para evitar bloqueo del preview
        private Task? _currentFinalProcessingTask = null;
        private CancellationTokenSource? _currentFinalProcessingCts = null;
        private readonly object _finalProcessingLock = new object();
        
        // Bloqueo robusto para captura final: SemaphoreSlim(1,1) - NUNCA dos capturas simultáneas
        private readonly System.Threading.SemaphoreSlim _captureFinalSemaphore = new System.Threading.SemaphoreSlim(1, 1);
        private CancellationTokenSource? _captureFinalCts = null;
        
        // FPS tracking para Phase4A
        private int _frameCount4A = 0;
        private DateTime _fpsStartTime = DateTime.Now;
        
        // Preview performance counters y throttling
        private BitmapSource? _latestFrame = null;
        private readonly object _latestFrameLock = new object();
        private bool _isRendering = false;
        private int _droppedFrames = 0;
        private DateTime _lastPreviewLogTime = DateTime.Now;
        private readonly TimeSpan _previewLogInterval = TimeSpan.FromSeconds(2);
        
        // Control de pipelines: Preview vs Final
        // PREVIEW SIEMPRE RAW: Prohibido procesamiento en preview
        private const bool _enablePreviewProcessing = false; // SIEMPRE false - preview 100% RAW sin procesamiento
        private bool _isFinalProcessingRunning = false; // Flag para saber si el pipeline final está corriendo
        
        // Performance timings por frame
        private readonly List<double> _copyFrameTimes = new List<double>();
        private readonly List<double> _convertTimes = new List<double>();
        private readonly List<double> _uiPresentTimes = new List<double>();
        private int _framesProcessed = 0;
        
        // Throttle: máximo 10 FPS (100ms por frame) para UI estable
        private readonly TimeSpan _minFrameInterval = TimeSpan.FromMilliseconds(100); // ~10 FPS
        private DateTime _lastFramePresented = DateTime.MinValue;

        public event PropertyChangedEventHandler? PropertyChanged;
        
        // Eventos separados: RAW para preview, FinalProcessed para captura final
        public event EventHandler<BitmapSource>? RawPreviewFrameUpdated;  // Solo frames RAW sin procesamiento
        public event EventHandler<BitmapSource>? FinalProcessedFrameReady; // Solo para captura final procesada
        
        // Mantener PreviewFrameUpdated por compatibilidad (deprecated - usar RawPreviewFrameUpdated)
        [Obsolete("Use RawPreviewFrameUpdated instead")]
        public event EventHandler<BitmapSource>? PreviewFrameUpdated;
        
        public event EventHandler? FlashRequested;

        private readonly StartupPhase _startupPhase;
        
        public KCMundialViewModel(StartupPhase phase = StartupPhase.Phase4_FullPipeline)
        {
            _startupPhase = phase;
            _phase4SubPhase = phase >= StartupPhase.Phase4_FullPipeline ? App.CURRENT_PHASE4_SUBPHASE : Phase4SubPhase.Phase4A_MarshalOnly;
            CrashLogger.Log($"VIEWMODEL_CONSTRUCTOR: Enter - Phase {phase}, Phase4SubPhase: {_phase4SubPhase}");
            
            // Phase1+: Crear servicios básicos (NO WinRT, NO cámara)
            if (_startupPhase >= StartupPhase.Phase1_NoCamera)
            {
                CrashLogger.Log("VIEWMODEL_CONSTRUCTOR: Creando servicios básicos (Collage, Storage)...");
                _collageService = new CollageService();
                _storageService = new StorageService();
                // Inicializar carpetas fijas determinísticas al inicio
                StorageService.InitializeDirectories();
                CrashLogger.Log("VIEWMODEL_CONSTRUCTOR: Servicios básicos creados");
                
                StartCommand = new RelayCommand(async () => await StartSession(), () => CanStart);
                SaveCommand = new RelayCommand(async () => await SavePhoto(), () => State == KCMundialState.Result);
                RestartCommand = new RelayCommand(() => ResetToIdle(), () => State == KCMundialState.Result);
            }
            
            // Phase2+: Crear CameraService (pero NO inicializar aún)
            if (_startupPhase >= StartupPhase.Phase2_CameraNoPreview)
            {
                CrashLogger.Log("VIEWMODEL_CONSTRUCTOR: Creando CameraService (Phase2+)...");
                _cameraService = new CameraService();
                CrashLogger.Log("VIEWMODEL_CONSTRUCTOR: CameraService creado");
            }
            
            // Phase3+: Registrar callback de frames (RAW, sin procesamiento)
            if (_startupPhase >= StartupPhase.Phase3_PreviewNoProcessing && _cameraService != null)
            {
                CrashLogger.Log("VIEWMODEL_CONSTRUCTOR: Registrando callback de frames RAW (Phase3)...");
                _cameraService.FrameCaptured += OnFrameCaptured;
                CrashLogger.Log("VIEWMODEL_CONSTRUCTOR: Callback de frames registrado");
            }
            
            // Phase4: Inicializar servicios de segmentación y procesamiento
            if (_startupPhase >= StartupPhase.Phase4_FullPipeline)
            {
                CrashLogger.Log("VIEWMODEL_CONSTRUCTOR: Inicializando servicios de segmentación (Phase4)...");
                _segmentationService = new Services.PersonSegmentationService();
                _removeBgService = new Services.RemoveBgService();
                
                // Inicializar servicios de reemplazo de fondo
                try
                {
                    _backgroundSegmentationService = new Services.BackgroundSegmentationService(_segmentationService);
                    _alphaPostProcessor = new Services.AlphaMattePostProcessor();
                    _backgroundComposer = new Services.BackgroundComposer();
                    _backgroundSegmentationService.Initialize();
                    
                    // Crear pipelines separados
                    _pipelinePreview = new Services.PipelinePreview(
                        _backgroundSegmentationService,
                        _alphaPostProcessor,
                        _segmentationService);
                    
                    _pipelineFinal = new Services.PipelineFinal(
                        _backgroundSegmentationService,
                        _alphaPostProcessor,
                        _segmentationService);
                    
                    CrashLogger.Log("VIEWMODEL_CONSTRUCTOR: Servicios de segmentación y pipelines inicializados");
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("VIEWMODEL_CONSTRUCTOR: Error inicializando servicios de reemplazo de fondo", ex);
                    System.Diagnostics.Debug.WriteLine($"Error inicializando servicios de reemplazo de fondo: {ex.Message}");
                    throw; // Re-throw para no ocultar
                }

                // Cargar background una vez para preview (Pipeline B)
                LoadBackground();
            }
            
            CrashLogger.Log($"VIEWMODEL_CONSTRUCTOR: Exit - Phase {phase}");
        }
        
        /// <summary>
        /// Carga el background de camiseta una vez para reutilizar
        /// </summary>
        private void LoadBackground()
        {
            Task.Run(() =>
            {
                try
                {
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    var possiblePaths = new[]
                    {
                        Path.Combine(desktop, "KCMundial", "Assets", "frames", "arg.png"),
                        Path.Combine(AppContext.BaseDirectory, "Assets", "frames", "arg.png")
                    };

                    string? backgroundPath = null;
                    foreach (var path in possiblePaths)
                    {
                        if (File.Exists(path))
                        {
                            backgroundPath = path;
                            break;
                        }
                    }

                    if (!string.IsNullOrEmpty(backgroundPath))
                    {
                        using (var stream = File.OpenRead(backgroundPath))
                        {
                            _backgroundBitmap = SkiaSharp.SKBitmap.Decode(stream);
                            System.Diagnostics.Debug.WriteLine($"✓ Background cargado: {backgroundPath}");
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("⚠ No se encontró background, usando fondo blanco");
                        // Crear fondo blanco como fallback
                        _backgroundBitmap = new SkiaSharp.SKBitmap(531, 709, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque);
                        _backgroundBitmap.Erase(SkiaSharp.SKColors.White);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error al cargar background: {ex.Message}");
                }
            });
        }

        public KCMundialState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsIdle));
                    OnPropertyChanged(nameof(IsPreviewing));
                    OnPropertyChanged(nameof(IsPreviewActive));
                    OnPropertyChanged(nameof(IsCountdown));
                    OnPropertyChanged(nameof(IsResult));
                }
            }
        }

        public bool IsIdle => State == KCMundialState.Idle || State == KCMundialState.Error;
        public bool IsPreviewing => true; // Siempre mostrar preview - en todos los estados
        public bool IsPreviewActive => State == KCMundialState.Previewing || State == KCMundialState.Countdown || State == KCMundialState.Capturing || State == KCMundialState.Idle;
        public bool IsCountdown => State == KCMundialState.Countdown;
        public bool IsResult => State == KCMundialState.Result;

        public string CountdownText
        {
            get => _countdownText;
            private set
            {
                _countdownText = value;
                OnPropertyChanged();
            }
        }

        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                _errorMessage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public string ResultStripPath
        {
            get => _resultStripPath;
            private set
            {
                _resultStripPath = value;
                OnPropertyChanged();
                UpdateResultStripImage();
            }
        }

        public System.Windows.Media.Imaging.BitmapImage? ResultStripImage
        {
            get => _resultStripImage;
            private set
            {
                _resultStripImage = value;
                OnPropertyChanged();
            }
        }

        private void UpdateResultStripImage()
        {
            if (string.IsNullOrEmpty(_resultStripPath) || !System.IO.File.Exists(_resultStripPath))
            {
                ResultStripImage = null;
                return;
            }

            try
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(_resultStripPath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                ResultStripImage = bitmap;
            }
            catch
            {
                ResultStripImage = null;
            }
        }

        public bool CanStart
        {
            get => _canStart;
            private set
            {
                _canStart = value;
                OnPropertyChanged();
                ((RelayCommand)StartCommand).RaiseCanExecuteChanged();
            }
        }

        public ICommand StartCommand { get; }
        public ICommand SaveCommand { get; }
        public ICommand RestartCommand { get; }

        public ObservableCollection<CameraInfo> AvailableCameras
        {
            get => _availableCameras;
            private set
            {
                if (_availableCameras != value)
                {
                    _availableCameras = value;
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        OnPropertyChanged(nameof(AvailableCameras));
                        System.Diagnostics.Debug.WriteLine($"AvailableCameras actualizado en UI: {value?.Count ?? 0} cámaras");
                    });
                }
            }
        }

        public CameraInfo? SelectedCamera
        {
            get => _selectedCamera;
            set
            {
                // Si es la misma cámara, no hacer nada
                if (_selectedCamera?.DeviceId == value?.DeviceId)
                {
                    return;
                }
                
                var previousCamera = _selectedCamera;
                _selectedCamera = value;
                OnPropertyChanged();
                
                // Actualizar CanStart inmediatamente - solo necesita cámara seleccionada
                CanStart = _selectedCamera != null;
                
                // CAMBIAR CÁMARA - solo si el usuario cambia manualmente (no en la primera carga)
                if (_selectedCamera != null && previousCamera != null && previousCamera.DeviceId != _selectedCamera.DeviceId)
                {
                    // Deshabilitar botón temporalmente mientras se cambia la cámara
                    CanStart = false;
                    
                    // Cambiar cámara - ejecutar en UI thread (STA) para MediaCapture
                    _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            System.Diagnostics.Debug.WriteLine($"Cambiando de cámara: {previousCamera.Name} -> {_selectedCamera?.Name}");
                            
                            // PRIMERO: Detener preview anterior
                            await _cameraService.StopPreviewAsync();
                            
                            // LUEGO: Inicializar nueva cámara
                            if (_selectedCamera?.DeviceId != null)
                            {
                                await PreInitializeCamera(_selectedCamera.DeviceId);
                                
                                // Actualizar CanStart después de inicializar
                                CanStart = _selectedCamera != null && _cameraService.IsInitialized;
                                System.Diagnostics.Debug.WriteLine($"✓ Cámara cambiada exitosamente, CanStart: {CanStart}");
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error al cambiar cámara: {ex.Message}");
                            System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                            // En caso de error, habilitar de nuevo si hay cámara seleccionada
                            CanStart = _selectedCamera != null;
                        }
                    }, System.Windows.Threading.DispatcherPriority.Normal);
                }
                else if (_selectedCamera != null && previousCamera == null)
                {
                    // Primera selección de cámara - asegurar que CanStart esté habilitado
                    CanStart = _selectedCamera != null;
                }
            }
        }

        public List<FrameInfo> AvailableFrames
        {
            get => _availableFrames;
            set
            {
                _availableFrames = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFrames));
            }
        }

        public bool HasFrames => _availableFrames != null && _availableFrames.Count > 0;

        public FrameInfo? SelectedFrame
        {
            get => _selectedFrame;
            set
            {
                _selectedFrame = value;
                OnPropertyChanged();
                CanStart = _selectedCamera != null; // Solo necesita cámara seleccionada
                
                // Notificar al MainWindow para actualizar la selección visual
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
                    mainWindow?.UpdateFrameSelection();
                });
            }
        }

        public List<DesignInfo> AvailableDesigns
        {
            get => _availableDesigns;
            set
            {
                _availableDesigns = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDesigns));
            }
        }

        public bool HasDesigns => _availableDesigns != null && _availableDesigns.Count > 0;

        public DesignInfo? SelectedDesign
        {
            get => _selectedDesign;
            set
            {
                _selectedDesign = value;
                OnPropertyChanged();
                CanStart = _selectedCamera != null; // Solo necesita cámara seleccionada
                
                // Notificar al MainWindow para actualizar la selección visual
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    var mainWindow = System.Windows.Application.Current.MainWindow as MainWindow;
                    mainWindow?.UpdateDesignSelection();
                });
            }
        }

        public ObservableCollection<string> AvailablePrinters
        {
            get => _availablePrinters;
            set
            {
                _availablePrinters = value;
                OnPropertyChanged();
            }
        }

        public string? SelectedPrinter
        {
            get => _selectedPrinter;
            set
            {
                _selectedPrinter = value;
                OnPropertyChanged();
                SavePrinterSelection();
            }
        }
        
        private async Task PreInitializeCamera(string deviceId)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CrashLogger.Log($"PREINITIALIZE_CAMERA: Enter - Phase {_startupPhase}, deviceId: {deviceId}");
            try
            {
                System.Diagnostics.Debug.WriteLine($"PreInitializeCamera: Inicio - deviceId: {deviceId} ({sw.ElapsedMilliseconds} ms)");
                
                // Inicializar directamente en el UI thread (STA) - ya estamos en STA desde InitializeAsync
                // NO usar Dispatcher.InvokeAsync - ya estamos en el thread correcto
                if (await _cameraService.InitializeAsync(deviceId))
                {
                    System.Diagnostics.Debug.WriteLine($"PreInitializeCamera: Cámara inicializada ({sw.ElapsedMilliseconds} ms)");
                    CrashLogger.Log("PREINITIALIZE_CAMERA: MediaCapture inicializado");
                    
                    // Phase3+: Iniciar preview (Phase2 NO inicia preview)
                    if (_startupPhase >= StartupPhase.Phase3_PreviewNoProcessing)
                    {
                        CrashLogger.Log("PREINITIALIZE_CAMERA: Iniciando preview RAW...");
                        _cameraService.StartPreview();
                        System.Diagnostics.Debug.WriteLine($"PreInitializeCamera: Preview iniciado ({sw.ElapsedMilliseconds} ms)");
                        CrashLogger.Log("PREINITIALIZE_CAMERA: Preview iniciado (esperando primer frame...)");
                    }
                    else
                    {
                        CrashLogger.Log("PREINITIALIZE_CAMERA: Preview NO iniciado (Phase2)");
                    }
                    
                    // Asegurar que CanStart esté habilitado después de inicializar
                    CanStart = _selectedCamera != null && _cameraService.IsInitialized;
                    System.Diagnostics.Debug.WriteLine($"PreInitializeCamera: CanStart actualizado a {CanStart}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"PreInitializeCamera: Error al inicializar ({sw.ElapsedMilliseconds} ms)");
                    CrashLogger.Log("PREINITIALIZE_CAMERA: Error al inicializar MediaCapture");
                    CanStart = false;
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("PREINITIALIZE_CAMERA: EXCEPCIÓN", ex);
                System.Diagnostics.Debug.WriteLine($"PreInitializeCamera: EXCEPCIÓN en {sw.ElapsedMilliseconds} ms");
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                CanStart = false;
                throw; // Re-throw para no ocultar
            }
            CrashLogger.Log($"PREINITIALIZE_CAMERA: Exit - Phase {_startupPhase}");
        }

        public async Task InitializeAsync()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            CrashLogger.Log($"VIEWMODEL_INITIALIZEASYNC: Enter - Phase {_startupPhase}");
            System.Diagnostics.Debug.WriteLine("=== InitializeAsync: INICIO ===");
            
            // Inicializar UI INMEDIATAMENTE
            State = KCMundialState.Idle;
            CanStart = false;
            
            // Phase1: Solo UI, sin cámara
            if (_startupPhase == StartupPhase.Phase1_NoCamera)
            {
                CrashLogger.Log("VIEWMODEL_INITIALIZEASYNC: Phase1 - Inicializando servicios básicos (sin cámara)...");
                try
                {
                    // Cargar impresoras disponibles (no requiere WinRT)
                    LoadAvailablePrinters();
                    CrashLogger.Log("PHASE1: READY");
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("VIEWMODEL_INITIALIZEASYNC: Phase1 - Error", ex);
                    throw; // Re-throw para no ocultar
                }
                return;
            }
            
            // Phase2+: Detectar cámaras
            if (_startupPhase >= StartupPhase.Phase2_CameraNoPreview)
            {
                try
                {
                    CrashLogger.Log($"VIEWMODEL_INITIALIZEASYNC: Detectando cámaras...");
                    System.Diagnostics.Debug.WriteLine($"Init: Detectando cámaras... ({sw.ElapsedMilliseconds} ms)");
                    var cameras = await CameraService.GetAvailableCamerasAsync();
                    System.Diagnostics.Debug.WriteLine($"Init: Cámaras detectadas: {cameras.Count} ({sw.ElapsedMilliseconds} ms)");
                    CrashLogger.Log($"VIEWMODEL_INITIALIZEASYNC: Cámaras detectadas: {cameras.Count}");
                    
                    if (cameras.Count == 0)
                    {
                        ErrorMessage = "No se encontró ninguna webcam conectada.";
                        State = KCMundialState.Error;
                        _availableCameras.Clear();
                        OnPropertyChanged(nameof(AvailableCameras));
                        return;
                    }

                    // Actualizar AvailableCameras - ya estamos en UI thread
                    var newCollection = new ObservableCollection<CameraInfo>(cameras);
                    _availableCameras = newCollection;
                    OnPropertyChanged(nameof(AvailableCameras));
                    System.Diagnostics.Debug.WriteLine($"AvailableCameras actualizado: {_availableCameras.Count} cámaras");
                    
                    // Phase2: Inicializar MediaCapture pero NO iniciar preview
                    if (_startupPhase == StartupPhase.Phase2_CameraNoPreview)
                    {
                        if (cameras.Count > 0 && cameras[0].DeviceId != null)
                        {
                            CrashLogger.Log("PHASE2: CAMERA_INIT_BEGIN");
                            System.Diagnostics.Debug.WriteLine($"Init: Inicializando MediaCapture (sin preview)... ({sw.ElapsedMilliseconds} ms)");
                            SelectedCamera = cameras[0];
                            
                            // Inicializar MediaCapture pero NO llamar a StartPreview
                            if (await _cameraService.InitializeAsync(cameras[0].DeviceId))
                            {
                                CrashLogger.Log("PHASE2: CAMERA_INIT_OK");
                                CrashLogger.Log("PHASE2: READY");
                                System.Diagnostics.Debug.WriteLine($"Init: MediaCapture inicializado (sin preview) ({sw.ElapsedMilliseconds} ms)");
                            }
                            else
                            {
                                CrashLogger.Log("PHASE2: CAMERA_INIT_FAILED");
                            }
                        }
                        else
                        {
                            CrashLogger.Log("PHASE2: NO_CAMERAS_FOUND");
                        }
                        return;
                    }
                    
                    // Phase3: Inicializar cámara y preview RAW (sin procesamiento)
                    if (_startupPhase == StartupPhase.Phase3_PreviewNoProcessing && cameras.Count > 0 && cameras[0].DeviceId != null)
                    {
                        CrashLogger.Log("PHASE3: PREVIEW_START");
                        System.Diagnostics.Debug.WriteLine($"Init: Inicializando primera cámara con preview RAW... ({sw.ElapsedMilliseconds} ms)");
                        SelectedCamera = cameras[0];
                        
                        // Inicializar cámara en UI thread (STA) - NO en Task.Run
                        await PreInitializeCamera(cameras[0].DeviceId);
                        System.Diagnostics.Debug.WriteLine($"Init: Primera cámara inicializada ({sw.ElapsedMilliseconds} ms)");
                        CrashLogger.Log("PHASE3: READY");
                    }
                    // Phase4+: Inicializar cámara y preview con procesamiento
                    else if (_startupPhase >= StartupPhase.Phase4_FullPipeline && cameras.Count > 0 && cameras[0].DeviceId != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"Init: Inicializando primera cámara... ({sw.ElapsedMilliseconds} ms)");
                        SelectedCamera = cameras[0];
                        
                        // Inicializar cámara en UI thread (STA) - NO en Task.Run
                        await PreInitializeCamera(cameras[0].DeviceId);
                        System.Diagnostics.Debug.WriteLine($"Init: Primera cámara inicializada ({sw.ElapsedMilliseconds} ms)");
                    }
                    
                    // Phase4: Inicializar servicio de segmentación
                    if (_startupPhase >= StartupPhase.Phase4_FullPipeline && _segmentationService != null)
                    {
                        CrashLogger.Log("VIEWMODEL_INITIALIZEASYNC: Phase4 - Inicializando servicio de segmentación");
                        _segmentationService.Initialize();
                    }
                    
                    // Ya no necesitamos seleccionar frame ni diseño - solo cámara
                    CanStart = SelectedCamera != null;
                    System.Diagnostics.Debug.WriteLine($"=== InitializeAsync: COMPLETADO en {sw.ElapsedMilliseconds} ms ===");
                    CrashLogger.Log($"VIEWMODEL_INITIALIZEASYNC: Exit - Phase {_startupPhase}");
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("VIEWMODEL_INITIALIZEASYNC: ERROR", ex);
                    System.Diagnostics.Debug.WriteLine($"=== InitializeAsync: ERROR en {sw.ElapsedMilliseconds} ms ===");
                    System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                    ErrorMessage = $"Error al detectar cámaras: {ex.Message}";
                    State = KCMundialState.Error;
                    _availableCameras.Clear();
                    OnPropertyChanged(nameof(AvailableCameras));
                }
            }
        }

        public void LoadAvailableFrames()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== LoadAvailableFrames INICIADO ===");
                var frames = new List<FrameInfo>();
                
                // Buscar Assets - Desktop/KCMundial/Assets/frames
                var possiblePaths = new List<string>();
                
                // 1. Desktop/KCMundial/Assets/frames (PRINCIPAL)
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                possiblePaths.Add(Path.Combine(desktop, "KCMundial", "Assets", "frames"));
                
                // 2. Directorio del ejecutable/Assets/frames
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    var exeDir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        possiblePaths.Add(Path.Combine(exeDir, "Assets", "frames"));
                    }
                }
                
                // 3. AppContext.BaseDirectory/Assets/frames
                possiblePaths.Add(Path.Combine(AppContext.BaseDirectory, "Assets", "frames"));
                
                string? framesDir = null;
                foreach (var path in possiblePaths.Distinct())
                {
                    System.Diagnostics.Debug.WriteLine($"Verificando: {path}");
                    if (Directory.Exists(path))
                    {
                        framesDir = path;
                        System.Diagnostics.Debug.WriteLine($"✓✓✓ ENCONTRADO: {path}");
                        break;
                    }
                }
                
                if (framesDir == null)
                {
                    System.Diagnostics.Debug.WriteLine($"✗✗✗ NO ENCONTRADO. Buscado en: {string.Join(", ", possiblePaths)}");
                    AvailableFrames = new List<FrameInfo>();
                    return;
                }
                
                var imageExtensions = new[] { ".png", ".jpg", ".jpeg", ".bmp" };
                var frameFiles = Directory.GetFiles(framesDir)
                    .Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => 
                    {
                        // Ordenar numéricamente: 1.png, 2.png, ..., 10.png, 11.png
                        var fileName = Path.GetFileNameWithoutExtension(f);
                        if (int.TryParse(fileName, out int num))
                            return num;
                        return int.MaxValue;
                    })
                    .ToList();

                System.Diagnostics.Debug.WriteLine($"Archivos: {frameFiles.Count}");

                foreach (var frameFile in frameFiles)
                {
                    try
                    {
                        var frameInfo = new FrameInfo
                        {
                            FileName = Path.GetFileName(frameFile),
                            FullPath = frameFile
                        };

                        // Crear miniatura
                        var thumbnail = new System.Windows.Media.Imaging.BitmapImage();
                        thumbnail.BeginInit();
                        thumbnail.UriSource = new Uri(frameFile, UriKind.Absolute);
                        thumbnail.DecodePixelWidth = 150;
                        thumbnail.DecodePixelHeight = 150;
                        thumbnail.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        thumbnail.EndInit();
                        thumbnail.Freeze();
                        
                        frameInfo.Thumbnail = thumbnail;
                        frames.Add(frameInfo);
                        System.Diagnostics.Debug.WriteLine($"✓ {frameInfo.FileName}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ {frameFile}: {ex.Message}");
                    }
                }

                // ACTUALIZAR - esto dispara PropertyChanged
                // Si ya estamos en UI thread, no necesitamos Dispatcher
                try
                {
                    AvailableFrames = frames;
                    System.Diagnostics.Debug.WriteLine($"✓✓✓ TOTAL: {frames.Count} frames asignados");
                    System.Diagnostics.Debug.WriteLine($"✓✓✓ HasFrames: {HasFrames}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error al asignar AvailableFrames: {ex.Message}");
                    AvailableFrames = new List<FrameInfo>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗✗✗ ERROR: {ex.Message}");
                AvailableFrames = new List<FrameInfo>();
            }
        }

        public void LoadAvailablePrinters()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== LoadAvailablePrinters INICIADO ===");
                var printers = new List<string>();

                using (var printServer = new LocalPrintServer())
                {
                    var printQueues = printServer.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
                    
                    foreach (var queue in printQueues)
                    {
                        printers.Add(queue.Name);
                        System.Diagnostics.Debug.WriteLine($"✓ Impresora encontrada: {queue.Name}");
                    }
                }

                // Actualizar colección
                var newCollection = new ObservableCollection<string>(printers);
                _availablePrinters = newCollection;
                OnPropertyChanged(nameof(AvailablePrinters));
                
                System.Diagnostics.Debug.WriteLine($"✓✓✓ TOTAL: {printers.Count} impresoras asignadas");

                // Cargar impresora guardada o seleccionar la primera
                LoadPrinterSelection();
                
                if (string.IsNullOrEmpty(SelectedPrinter) && printers.Count > 0)
                {
                    SelectedPrinter = printers[0];
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗✗✗ ERROR al cargar impresoras: {ex.Message}");
                AvailablePrinters = new ObservableCollection<string>();
            }
        }

        private void SavePrinterSelection()
        {
            try
            {
                if (string.IsNullOrEmpty(_selectedPrinter)) return;

                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var configPath = Path.Combine(desktop, "KCMundial", "printer.txt");
                
                // Asegurar que el directorio existe
                var configDir = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
                {
                    Directory.CreateDirectory(configDir);
                }

                File.WriteAllText(configPath, _selectedPrinter);
                System.Diagnostics.Debug.WriteLine($"✓ Impresora guardada: {_selectedPrinter}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al guardar impresora: {ex.Message}");
            }
        }

        private void LoadPrinterSelection()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var configPath = Path.Combine(desktop, "KCMundial", "printer.txt");

                if (File.Exists(configPath))
                {
                    var savedPrinter = File.ReadAllText(configPath).Trim();
                    if (!string.IsNullOrEmpty(savedPrinter) && _availablePrinters.Contains(savedPrinter))
                    {
                        _selectedPrinter = savedPrinter;
                        OnPropertyChanged(nameof(SelectedPrinter));
                        System.Diagnostics.Debug.WriteLine($"✓ Impresora cargada: {savedPrinter}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al cargar impresora: {ex.Message}");
            }
        }

        public void LoadAvailableDesigns()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("=== LoadAvailableDesigns INICIADO ===");
                var designs = new List<DesignInfo>();
                
                // Buscar Assets - Desktop/KCMundial/Assets/designs
                var possiblePaths = new List<string>();
                
                // 1. Desktop/KCMundial/Assets/designs (PRINCIPAL)
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                possiblePaths.Add(Path.Combine(desktop, "KCMundial", "Assets", "designs"));
                
                // 2. Directorio del ejecutable/Assets/designs
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath != null)
                {
                    var exeDir = Path.GetDirectoryName(exePath);
                    if (!string.IsNullOrEmpty(exeDir))
                    {
                        possiblePaths.Add(Path.Combine(exeDir, "Assets", "designs"));
                    }
                }
                
                // 3. AppContext.BaseDirectory/Assets/designs
                possiblePaths.Add(Path.Combine(AppContext.BaseDirectory, "Assets", "designs"));
                
                string? designsDir = null;
                foreach (var path in possiblePaths.Distinct())
                {
                    System.Diagnostics.Debug.WriteLine($"Verificando: {path}");
                    if (Directory.Exists(path))
                    {
                        designsDir = path;
                        System.Diagnostics.Debug.WriteLine($"✓✓✓ ENCONTRADO: {path}");
                        break;
                    }
                }
                
                if (designsDir == null)
                {
                    System.Diagnostics.Debug.WriteLine($"✗✗✗ NO ENCONTRADO. Buscado en: {string.Join(", ", possiblePaths)}");
                    AvailableDesigns = new List<DesignInfo>();
                    return;
                }
                
                // Buscar archivos de imagen
                var imageExtensions = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif" };
                var designFiles = new List<string>();
                foreach (var ext in imageExtensions)
                {
                    designFiles.AddRange(Directory.GetFiles(designsDir, ext, SearchOption.TopDirectoryOnly));
                }
                
                System.Diagnostics.Debug.WriteLine($"Archivos encontrados: {designFiles.Count}");
                
                foreach (var designFile in designFiles.OrderBy(f => f))
                {
                    try
                    {
                        // Determinar tipo de diseño basado en el nombre del archivo
                        var fileName = Path.GetFileNameWithoutExtension(designFile).ToLower();
                        PrintDesignType designType;
                        
                        if (fileName.Contains("strip") || fileName.Contains("3") || fileName.Contains("vertical"))
                        {
                            designType = PrintDesignType.Strip3Photos;
                        }
                        else if (fileName.Contains("instant") || fileName.Contains("1") || fileName.Contains("single"))
                        {
                            designType = PrintDesignType.InstantPhoto;
                        }
                        else if (fileName.Contains("horizontal") || fileName.Contains("2") || fileName.Contains("two"))
                        {
                            designType = PrintDesignType.TwoPhotosHorizontal;
                        }
                        else
                        {
                            // Por defecto, usar Strip3Photos
                            designType = PrintDesignType.Strip3Photos;
                        }
                        
                        var designInfo = new DesignInfo
                        {
                            DesignType = designType,
                            Name = Path.GetFileNameWithoutExtension(designFile),
                            Description = designType switch
                            {
                                PrintDesignType.Strip3Photos => "3 fotos verticales",
                                PrintDesignType.InstantPhoto => "1 foto estilo instantánea",
                                PrintDesignType.TwoPhotosHorizontal => "2 fotos con leyenda",
                                _ => "Diseño personalizado"
                            },
                            FullPath = designFile
                        };

                        // Crear miniatura cuadrada
                        var thumbnail = new System.Windows.Media.Imaging.BitmapImage();
                        thumbnail.BeginInit();
                        thumbnail.UriSource = new Uri(designFile, UriKind.Absolute);
                        thumbnail.DecodePixelWidth = 150;
                        thumbnail.DecodePixelHeight = 150;
                        thumbnail.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        thumbnail.EndInit();
                        thumbnail.Freeze();
                        
                        designInfo.Thumbnail = thumbnail;
                        designs.Add(designInfo);
                        System.Diagnostics.Debug.WriteLine($"✓ {designInfo.Name} ({designType})");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ {designFile}: {ex.Message}");
                    }
                }

                // ACTUALIZAR
                try
                {
                    AvailableDesigns = designs;
                    System.Diagnostics.Debug.WriteLine($"✓✓✓ TOTAL: {designs.Count} diseños asignados");
                    
                    // Seleccionar el primero por defecto
                    if (designs.Count > 0 && SelectedDesign == null)
                    {
                        SelectedDesign = designs[0];
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error al asignar AvailableDesigns: {ex.Message}");
                    AvailableDesigns = new List<DesignInfo>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗✗✗ ERROR: {ex.Message}");
                AvailableDesigns = new List<DesignInfo>();
            }
        }


        private async Task StartSession()
        {
            if (State != KCMundialState.Previewing && State != KCMundialState.Idle)
                return;

            if (_selectedCamera == null)
            {
                ErrorMessage = "Por favor selecciona una cámara.";
                return;
            }

            // EMPEZAR INMEDIATAMENTE - SIN ESPERAR NADA
            try
            {
                // NO hacer NADA con la cámara - ya está activa desde la sesión anterior
                // Solo asegurar que el preview esté activo (pero sin reinicializar)
                if (_cameraService.IsInitialized)
                {
                    // La cámara ya está lista - NO hacer StartPreview() porque puede causar reinicialización
                    // Solo cambiar el estado
                }
                
                // CAMBIAR ESTADO Y EMPEZAR COUNTDOWN INMEDIATAMENTE
                // NO ESPERAR que la cámara esté lista - empezar YA
                State = KCMundialState.Previewing;
                
                _cancellationTokenSource = new CancellationTokenSource();
                _capturedPhotos.Clear();
                _currentPhotoIndex = 0;

                // EMPEZAR COUNTDOWN INMEDIATAMENTE - La cámara YA está activa
                await CapturePhotoSequence(_cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                ResetToIdle();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error durante la sesión: {ex.Message}";
                State = KCMundialState.Error;
            }
        }

        private async Task CapturePhotoSequence(CancellationToken cancellationToken)
        {
            var captureSubPhase = App.CURRENT_CAPTURE_SUBPHASE;
            CrashLogger.Log($"CAPTURE_SEQUENCE: Enter - SubPhase {captureSubPhase}");
            
            try
            {
                // Solo capturar UNA foto para la figurita
                cancellationToken.ThrowIfCancellationRequested();

                _currentPhotoIndex = 0;

                // Countdown INMEDIATAMENTE - no esperar nada
                await ShowCountdown(cancellationToken);

                // Capture - la cámara debería estar lista, si no, esperará internamente
                cancellationToken.ThrowIfCancellationRequested();
                State = KCMundialState.Capturing;
                FlashRequested?.Invoke(this, EventArgs.Empty);

                // PAUSAR PREVIEW antes de capturar
                await _cameraService.PausePreviewAsync();
                
                try
                {
                    await Task.Delay(500, cancellationToken);
                    CrashLogger.Log("CAPTURE: Preview paused, capturing...");

                    // Procesar según subfase
                    string? photoPath = null;
                    
                    if (captureSubPhase == CaptureSubPhase.CAP0_RawStillOnly)
                    {
                        photoPath = await ProcessCaptureCAP0(cancellationToken);
                    }
                    else if (captureSubPhase == CaptureSubPhase.CAP1_SaveOnly)
                    {
                        photoPath = await ProcessCaptureCAP1(cancellationToken);
                    }
                    else if (captureSubPhase == CaptureSubPhase.CAP2_LoadSkia)
                    {
                        photoPath = await ProcessCaptureCAP2(cancellationToken);
                    }
                    else if (captureSubPhase == CaptureSubPhase.CAP3_ONNXOnly)
                    {
                        photoPath = await ProcessCaptureCAP3(cancellationToken);
                    }
                    else if (captureSubPhase == CaptureSubPhase.CAP4_FullPipeline)
                    {
                        // USAR CaptureAndProcessFinalAsync() - función única de captura final
                        // Bloquea reentradas automáticamente
                        var (alphaPath, cutoutPath) = await CaptureAndProcessFinalAsync(cancellationToken);
                        
                        if (string.IsNullOrEmpty(alphaPath) || string.IsNullOrEmpty(cutoutPath))
                        {
                            CrashLogger.Log("CAP4: CAPTURE_AND_PROCESS_FAILED");
                            throw new Exception("No se pudo capturar y procesar la foto");
                        }
                        
                        // Usar cutoutPath como photoPath para compatibilidad con código existente
                        photoPath = cutoutPath;
                        CrashLogger.Log($"CAP4: CAPTURE_AND_PROCESS_OK - Alpha: {alphaPath}, Cutout: {cutoutPath}");
                    }
                    
                    // CAP0 no guarda archivo, solo valida captura
                    if (captureSubPhase == CaptureSubPhase.CAP0_RawStillOnly)
                    {
                        CrashLogger.Log("CAPTURE_SEQUENCE: CAP0 completado - Solo validación de captura");
                        PlaySound("shutter");
                        ResetToIdle();
                        return;
                    }
                    
                    if (string.IsNullOrEmpty(photoPath))
                    {
                        CrashLogger.Log("CAPTURE_SEQUENCE: photoPath es null o vacío");
                        throw new Exception("Error al capturar la foto");
                    }

                    // Reproducir sonido de obturador
                    PlaySound("shutter");

                    _capturedPhotos.Add(photoPath);
                    CrashLogger.Log($"CAPTURE_SEQUENCE: Foto agregada a _capturedPhotos: {photoPath}");

                    // Solo procesar collage si es CAP4
                    if (captureSubPhase < CaptureSubPhase.CAP4_FullPipeline)
                    {
                        CrashLogger.Log($"CAPTURE_SEQUENCE: SubPhase {captureSubPhase} - Saltando procesamiento de collage");
                        ResetToIdle();
                        return;
                    }

                    // Compose collage (solo CAP4)
                    State = KCMundialState.Composing;
                    await ProcessCollageCAP4(photoPath, cancellationToken);
                }
                finally
                {
                    // SIEMPRE REANUDAR PREVIEW, incluso si hay excepción o cancelación
                    if (_cameraService != null)
                    {
                        try
                        {
                            await _cameraService.ResumePreviewAsync();
                        }
                        catch (Exception exResume)
                        {
                            CrashLogger.Log($"CAPTURE: Preview resume FAILED - {exResume.Message}", exResume);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Error al generar el collage: {ex.Message}";
                State = KCMundialState.Error;
            }
        }

        private async Task ShowCountdown(CancellationToken cancellationToken)
        {
            State = KCMundialState.Countdown;

            // Reproducir sonido de tick-tack UNA SOLA VEZ al inicio
            PlaySound("ticktack");

            // Countdown normal: 1 segundo por número
            for (int i = 3; i > 0; i--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CountdownText = i.ToString();
                await Task.Delay(1000, cancellationToken);
            }

            CountdownText = "";
        }

        private void OnFrameCaptured(object? sender, BitmapSource frame)
        {
            // PREVIEW 100% RAW: Solo copiar frame y actualizar UI, sin procesamiento
            // PROHIBIDO: PersonSegmentationService, BackgroundSegmentationService, AlphaMattePostProcessor
            
            // Latest-frame-wins: guardar solo el último frame
            BitmapSource? frameToProcess = null;
            lock (_latestFrameLock)
            {
                _latestFrame = frame;
                frameToProcess = _latestFrame; // Usar el último frame guardado
            }
            
            if (frameToProcess == null) return;
            
            // Throttle: máximo 10 FPS (100ms por frame)
            var now = DateTime.Now;
            if (now - _lastFramePresented < _minFrameInterval)
            {
                _droppedFrames++;
                return; // Descartar frame
            }
            
            // Si ya hay un render en curso, descartar este frame (latest-frame-wins)
            if (_isRendering)
            {
                _droppedFrames++;
                return;
            }
            
            // PREVIEW RAW: Solo copiar frame a WriteableBitmap y actualizar UI
            // Todo en background thread excepto la actualización de UI
            Task.Run(() =>
            {
                try
                {
                    // Usar el último frame guardado (puede ser más reciente que el recibido)
                    BitmapSource? latest = null;
                    lock (_latestFrameLock)
                    {
                        latest = _latestFrame;
                    }
                    
                    if (latest == null) return;
                    
                    // Procesar frame RAW (sin segmentación, sin alpha matte, sin postprocesado, sin Skia)
                    ProcessPreviewFrameRaw(latest);
                }
                catch (Exception ex)
                {
                    CrashLogger.Log("ONFRAMECAPTURED: Error en preview RAW", ex);
                    _isRendering = false;
                }
            });
        }
        
        // FPS tracking para RAW
        private int _rawFramesProcessed = 0;
        private DateTime _rawFpsStartTime = DateTime.Now;
        private int _rawFrameCount = 0; // Contador para logs cada 30 frames
        
        private void ProcessPreviewFrameRaw(BitmapSource frame)
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            var backgroundThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            
            try
            {
                // 1. Copiar frame (background thread) - MÍNIMO necesario
                var swCopy = System.Diagnostics.Stopwatch.StartNew();
                var width = frame.PixelWidth;
                var height = frame.PixelHeight;
                var stride = width * 4; // BGRA32
                var pixels = new byte[height * stride];
                
                frame.CopyPixels(pixels, stride, 0);
                swCopy.Stop();
                var copyTime = swCopy.Elapsed.TotalMilliseconds;
                
                // 2. Crear WriteableBitmap (background thread) - MÍNIMO necesario
                var swConvert = System.Diagnostics.Stopwatch.StartNew();
                var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                wb.Freeze();
                swConvert.Stop();
                var convertTime = swConvert.Elapsed.TotalMilliseconds;
                
                // 3. Actualizar contador y logs cada 30 frames
                _rawFrameCount++;
                _rawFramesProcessed++;
                var now = DateTime.Now;
                var elapsed = (now - _rawFpsStartTime).TotalSeconds;
                
                if (_rawFrameCount >= 30)
                {
                    var fps = _rawFramesProcessed / elapsed;
                    CrashLogger.Log($"PREVIEW_RAW_FRAME: FrameCount={_rawFrameCount}, FPS={fps:F1}, BackgroundThreadId={backgroundThreadId}, CopyTime={copyTime:F2}ms, ConvertTime={convertTime:F2}ms");
                    _rawFrameCount = 0;
                    _rawFramesProcessed = 0;
                    _rawFpsStartTime = now;
                }
                
                // 4. Presentar en UI (UI thread) - Solo actualización de ImageSource
                var swUI = System.Diagnostics.Stopwatch.StartNew();
                _isRendering = true;
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var uiThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    try
                    {
                        // Usar nuevo evento RawPreviewFrameUpdated (solo RAW)
                        RawPreviewFrameUpdated?.Invoke(this, wb);
                        
                        // Mantener compatibilidad con evento antiguo (deprecated)
                        #pragma warning disable CS0618 // Type or member is obsolete
                        PreviewFrameUpdated?.Invoke(this, wb);
                        #pragma warning restore CS0618
                        
                        _lastFramePresented = DateTime.Now;
                    }
                    finally
                    {
                        _isRendering = false;
                    }
                }, DispatcherPriority.Render);
                swUI.Stop();
                var uiTime = swUI.Elapsed.TotalMilliseconds;
                
                // Performance tracking
                _copyFrameTimes.Add(copyTime);
                _convertTimes.Add(convertTime);
                _uiPresentTimes.Add(uiTime);
                _framesProcessed++;
                _rawFramesProcessed++;
                
                // Log cada 2 segundos
                var nowLog = DateTime.Now;
                if (nowLog - _lastPreviewLogTime >= _previewLogInterval)
                {
                    if (_copyFrameTimes.Count > 0)
                    {
                        var avgCopy = _copyFrameTimes.Average();
                        var avgConvert = _convertTimes.Average();
                        var avgUI = _uiPresentTimes.Average();
                        var avgTotal = avgCopy + avgConvert + avgUI;
                        var fps = _framesProcessed / (now - _lastPreviewLogTime).TotalSeconds;
                        
                        CrashLogger.Log($"PREVIEW_STAGE_TIMINGS: Copy={avgCopy:F2}ms, Convert={avgConvert:F2}ms, UI={avgUI:F2}ms, Total={avgTotal:F2}ms");
                        CrashLogger.Log($"PREVIEW_FPS_REAL: {fps:F1} fps, Processed={_framesProcessed}, Dropped={_droppedFrames}");
                        CrashLogger.Log($"PREVIEW_DROP_COUNT: Dropped={_droppedFrames}, Processed={_framesProcessed}, DropRate={(_droppedFrames * 100.0 / (_droppedFrames + _framesProcessed)):F1}%");
                        
                        // Reset counters
                        _copyFrameTimes.Clear();
                        _convertTimes.Clear();
                        _uiPresentTimes.Clear();
                        _framesProcessed = 0;
                        _droppedFrames = 0;
                        _lastPreviewLogTime = now;
                    }
                }
                
                // Log FPS RAW cada 2 segundos
                var rawElapsed = (now - _rawFpsStartTime).TotalSeconds;
                if (rawElapsed >= _previewLogInterval.TotalSeconds)
                {
                    var rawFps = _rawFramesProcessed / rawElapsed;
                    CrashLogger.Log($"PreviewFPS_RAW: {rawFps:F1} fps");
                    _rawFramesProcessed = 0;
                    _rawFpsStartTime = now;
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("PROCESSPREVIEWFRAMERAW: ERROR", ex);
                _isRendering = false;
            }
        }
        
        /// <summary>
        /// PipelinePreview: Procesamiento rápido para preview (baja resolución + postprocess mínimo)
        /// Delegado a IPipelinePreview - NO bloquea el preview - corre en background thread
        /// </summary>
        public BitmapSource? ProcessFrameForPreview(BitmapSource frame)
        {
            if (_pipelinePreview == null)
                return null;
            
            return _pipelinePreview.ProcessFrameForPreview(frame);
        }
        
        /// <summary>
        /// PipelineFinal: Procesamiento completo para foto final (alta resolución + postprocess completo)
        /// Delegado a IPipelineFinal - NO bloquea el preview - corre completamente en background thread
        /// El preview seguirá funcionando (RAW o procesado rápido) mientras este método corre
        /// </summary>
        public async Task<(string? alphaPath, string? cutoutPath)> ProcessFrameForFinal(
            BitmapSource stillFrame, 
            string outputFolder,
            CancellationToken cancellationToken = default)
        {
            if (_pipelineFinal == null)
            {
                CrashLogger.Log("PIPELINE_FINAL: INVALID_INPUT - pipeline not initialized");
                return (null, null);
            }
            
            // Delegar al pipeline final - SIEMPRE corre en background thread
            return await _pipelineFinal.ProcessFrameForFinal(stillFrame, outputFolder, cancellationToken);
        }
        
        /// <summary>
        /// Captura y procesa foto final con segmentación remove.bg-like (alpha continuo + postprocesado completo)
        /// SOLO se ejecuta al sacar la foto, nunca en preview
        /// Bloquea reentradas (no permite doble click)
        /// </summary>
        public async Task<(string? alphaPath, string? cutoutPath)> CaptureAndProcessFinalAsync(CancellationToken cancellationToken = default)
        {
            // Bloqueo robusto: SemaphoreSlim(1,1) - NUNCA dos capturas simultáneas
            var acquired = await _captureFinalSemaphore.WaitAsync(0, cancellationToken);
            if (!acquired)
            {
                CrashLogger.Log("CAP_FINAL: REENTRY_BLOCKED - Captura ya en progreso (SemaphoreSlim)");
                return (null, null);
            }
            
            // Crear CancellationTokenSource para cancelación si el usuario cierra la app
            _captureFinalCts?.Cancel();
            _captureFinalCts?.Dispose();
            _captureFinalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var linkedCt = _captureFinalCts.Token;
            
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            var swWatchdog = System.Diagnostics.Stopwatch.StartNew();
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            
            // Log memoria inicial
            var memBefore = GC.GetTotalMemory(false);
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var workingSetBefore = process.WorkingSet64;
            CrashLogger.Log($"CAP_FINAL_BEGIN - ThreadId: {threadId}, MemBefore: {memBefore / 1024 / 1024}MB, WorkingSetBefore: {workingSetBefore / 1024 / 1024}MB");
            
            // PAUSAR PREVIEW ANTES DE CUALQUIER OPERACIÓN (estructura try/finally garantiza resume)
            bool previewWasRunning = false;
            if (_cameraService != null)
            {
                try
                {
                    previewWasRunning = !_cameraService.IsPreviewPaused;
                    if (previewWasRunning)
                    {
                        await _cameraService.PausePreviewAsync();
                        await System.Threading.Tasks.Task.Delay(500, linkedCt); // Pequeño delay para estabilizar
                    }
                }
                catch (Exception exPause)
                {
                    CrashLogger.Log($"CAP_FINAL: PAUSE_PREVIEW_FAILED - {exPause.Message}", exPause);
                    // Continuar de todas formas - el finally reanudará si es necesario
                }
            }
            
            // Variables para dispose correcto
            Windows.Graphics.Imaging.SoftwareBitmap? softwareBitmap = null;
            Windows.Graphics.Imaging.SoftwareBitmap? convertedBitmap = null;
            SkiaSharp.SKBitmap? originalBitmap = null;
            SkiaSharp.SKBitmap? alphaMask = null;
            SkiaSharp.SKBitmap? processedMask = null;
            SkiaSharp.SKBitmap? finalMask = null;
            SkiaSharp.SKBitmap? cutoutBitmap = null;
            SkiaSharp.SKImage? alphaImage = null;
            SkiaSharp.SKImage? cutoutImage = null;
            SkiaSharp.SKData? alphaData = null;
            SkiaSharp.SKData? cutoutData = null;
            string? fallbackPath = null;
            
            try
            {
                // Watchdog: Si tarda > 10s, loggear warning
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10000, linkedCt);
                    if (!linkedCt.IsCancellationRequested && swWatchdog.IsRunning)
                    {
                        CrashLogger.Log($"CAP_FINAL: WARNING_SLOW_CAPTURE - Tiempo transcurrido: {swWatchdog.ElapsedMilliseconds}ms");
                    }
                }, linkedCt);
                
                // Verificar servicios inicializados
                if (_segmentationService == null || _alphaPostProcessor == null || _storageService == null || _cameraService == null)
                {
                    CrashLogger.Log("CAP_FINAL: SERVICES_NOT_INITIALIZED");
                    throw new Exception("Servicios de segmentación no inicializados");
                }
                
                if (!_segmentationService.IsInitialized)
                {
                    CrashLogger.Log("CAP_FINAL: SEGMENTATION_NOT_INITIALIZED");
                    throw new Exception("Servicio de segmentación no inicializado");
                }
                
                linkedCt.ThrowIfCancellationRequested();
                    
                    // 1. Capturar still frame en máxima resolución disponible
                    CrashLogger.Log("CAP_FINAL: CAPTURE_STILL_BEGIN");
                    var swCapture = System.Diagnostics.Stopwatch.StartNew();
                    
                    // Intentar capturar en máxima resolución usando LowLagPhotoCapture
                    try
                    {
                        if (!_cameraService.IsInitialized)
                        {
                            throw new Exception("Cámara no inicializada");
                        }
                        
                        // Usar LowLagPhotoCapture para máxima resolución
                        var mediaCapture = _cameraService.GetMediaCapture();
                        if (mediaCapture == null)
                        {
                            throw new Exception("MediaCapture no disponible");
                        }
                        
                        // Preparar captura en máxima resolución (sin comprimir)
                        var imageEncodingProps = Windows.Media.MediaProperties.ImageEncodingProperties.CreateUncompressed(Windows.Media.MediaProperties.MediaPixelFormat.Bgra8);
                        var lowLagCapture = await mediaCapture.PrepareLowLagPhotoCaptureAsync(imageEncodingProps);
                        
                        try
                        {
                            await System.Threading.Tasks.Task.Delay(300, linkedCt); // Pequeño delay para estabilizar
                            var capturedPhoto = await lowLagCapture.CaptureAsync();
                            softwareBitmap = capturedPhoto.Frame.SoftwareBitmap;
                            
                            if (softwareBitmap != null)
                            {
                                softwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap.Copy(softwareBitmap);
                            }
                        }
                        finally
                        {
                            await lowLagCapture.FinishAsync();
                        }
                    }
                    catch (Exception exCapture)
                    {
                        CrashLogger.Log($"CAP_FINAL: CAPTURE_STILL_FAILED - {exCapture.Message}", exCapture);
                        throw new Exception($"Error al capturar foto: {exCapture.Message}", exCapture);
                    }
                    
                    swCapture.Stop();
                    CrashLogger.Log($"CAP_FINAL: CAPTURE_STILL_END - Time: {swCapture.ElapsedMilliseconds}ms, Size: {softwareBitmap?.PixelWidth}x{softwareBitmap?.PixelHeight}");
                    
                    if (softwareBitmap == null)
                    {
                        throw new Exception("No se pudo capturar foto");
                    }
                    
                    linkedCt.ThrowIfCancellationRequested();
                
                // 2. Convertir SoftwareBitmap a SKBitmap en BGRA8888
                CrashLogger.Log("CAP_FINAL: CONVERT_TO_SKBITMAP_BEGIN");
                var swConvert = System.Diagnostics.Stopwatch.StartNew();
                
                try
                {
                    // Convertir SoftwareBitmap a SKBitmap usando método seguro
                    var width = softwareBitmap.PixelWidth;
                    var height = softwareBitmap.PixelHeight;
                    
                    // Convertir SoftwareBitmap a formato compatible
                    if (softwareBitmap.BitmapPixelFormat != Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8)
                    {
                        convertedBitmap = Windows.Graphics.Imaging.SoftwareBitmap.Convert(softwareBitmap, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8);
                    }
                    else
                    {
                        convertedBitmap = Windows.Graphics.Imaging.SoftwareBitmap.Copy(softwareBitmap);
                    }
                    
                    // Usar BitmapDecoder para obtener pixels (método confiable)
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId, stream);
                        encoder.SetSoftwareBitmap(convertedBitmap);
                        await encoder.FlushAsync();
                        
                        stream.Seek(0);
                        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                        var pixelData = await decoder.GetPixelDataAsync();
                        var pixels = pixelData.DetachPixelData();
                        
                        // Crear SKBitmap en BGRA8888
                        var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                        originalBitmap = new SkiaSharp.SKBitmap(info);
                        
                        unsafe
                        {
                            var ptr = (byte*)originalBitmap.GetPixels();
                            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, new IntPtr(ptr), pixels.Length);
                        }
                    }
                }
                catch (Exception exConvert)
                {
                    CrashLogger.Log($"CAP_FINAL: CONVERT_TO_SKBITMAP_FAILED - {exConvert.Message}", exConvert);
                    throw new Exception($"Error al convertir a SKBitmap: {exConvert.Message}", exConvert);
                }
                
                swConvert.Stop();
                CrashLogger.Log($"CAP_FINAL: CONVERT_TO_SKBITMAP_END - Time: {swConvert.ElapsedMilliseconds}ms");
                
                if (originalBitmap == null || originalBitmap.IsNull)
                {
                    throw new Exception("No se pudo convertir foto a SKBitmap");
                }
                
                linkedCt.ThrowIfCancellationRequested();
                
                // 3. Segmentación -> alpha matte continuo (0-255) usando GetAlphaMatte()
                // FALLBACK: Si falla, guardar foto original sin recorte
                CrashLogger.Log("CAP_FINAL: SEG_BEGIN");
                var swSeg = System.Diagnostics.Stopwatch.StartNew();
                
                try
                {
                    alphaMask = await System.Threading.Tasks.Task.Run(() =>
                    {
                        return _segmentationService.GetAlphaMatte(originalBitmap);
                    }, linkedCt);
                }
                catch (Exception exSeg)
                {
                    CrashLogger.Log($"CAP_FINAL: SEG_FAILED - {exSeg.Message}", exSeg);
                    
                    // FALLBACK: Guardar foto original sin recorte
                    try
                    {
                        var fallbackSessionPath1 = await _storageService.CreateSessionFolderAsync();
                        var fallbackTimestamp1 = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        fallbackPath = Path.Combine(fallbackSessionPath1, $"{fallbackTimestamp1}_original_fallback.png");
                        
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            using (var image = SkiaSharp.SKImage.FromBitmap(originalBitmap))
                            using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                            using (var stream = File.Create(fallbackPath))
                            {
                                data.SaveTo(stream);
                            }
                        }, linkedCt);
                        
                        CrashLogger.Log($"CAP_FINAL_FALLBACK - Segmentación falló, guardada foto original: {fallbackPath}", exSeg);
                        return (null, fallbackPath);
                    }
                    catch (Exception exFallback)
                    {
                        CrashLogger.Log($"CAP_FINAL: FALLBACK_FAILED - {exFallback.Message}", exFallback);
                        throw new Exception($"Error en segmentación y fallback: {exSeg.Message}", exSeg);
                    }
                }
                
                swSeg.Stop();
                CrashLogger.Log($"CAP_FINAL: SEG_END - Time: {swSeg.ElapsedMilliseconds}ms, Size: {alphaMask?.Width}x{alphaMask?.Height}");
                
                if (alphaMask == null || alphaMask.IsNull)
                {
                    // FALLBACK: Guardar foto original sin recorte
                    try
                    {
                        var fallbackSessionPath2 = await _storageService.CreateSessionFolderAsync();
                        var fallbackTimestamp2 = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        fallbackPath = Path.Combine(fallbackSessionPath2, $"{fallbackTimestamp2}_original_fallback.png");
                        
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            using (var image = SkiaSharp.SKImage.FromBitmap(originalBitmap))
                            using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                            using (var stream = File.Create(fallbackPath))
                            {
                                data.SaveTo(stream);
                            }
                        }, linkedCt);
                        
                        CrashLogger.Log($"CAP_FINAL_FALLBACK - Máscara alpha null, guardada foto original: {fallbackPath}");
                        return (null, fallbackPath);
                    }
                    catch (Exception exFallback)
                    {
                        CrashLogger.Log($"CAP_FINAL: FALLBACK_FAILED - {exFallback.Message}", exFallback);
                        throw new Exception("No se pudo generar máscara alpha y fallback falló");
                    }
                }
                
                linkedCt.ThrowIfCancellationRequested();
                
                // 4. Postprocesado SOLO para final (ProcessForFinal)
                CrashLogger.Log("CAP_FINAL: POST_BEGIN");
                var swPost = System.Diagnostics.Stopwatch.StartNew();
                
                try
                {
                    processedMask = await System.Threading.Tasks.Task.Run(() =>
                    {
                        return _alphaPostProcessor.ProcessForFinal(alphaMask);
                    }, linkedCt);
                }
                catch (Exception exPost)
                {
                    CrashLogger.Log($"CAP_FINAL: POST_FAILED - {exPost.Message}", exPost);
                    // FALLBACK: Usar máscara sin postprocesar
                    processedMask = alphaMask.Copy();
                }
                
                swPost.Stop();
                CrashLogger.Log($"CAP_FINAL: POST_END - Time: {swPost.ElapsedMilliseconds}ms");
                
                if (processedMask == null || processedMask.IsNull)
                {
                    // FALLBACK: Usar máscara original sin postprocesar
                    processedMask = alphaMask.Copy();
                }
                
                // Redimensionar máscara al tamaño original si es necesario
                if (processedMask.Width != originalBitmap.Width || processedMask.Height != originalBitmap.Height)
                {
                    var maskInfo = new SkiaSharp.SKImageInfo(originalBitmap.Width, originalBitmap.Height, SkiaSharp.SKColorType.Alpha8, SkiaSharp.SKAlphaType.Opaque);
                    finalMask = processedMask.Resize(maskInfo, SkiaSharp.SKFilterQuality.High);
                    if (processedMask != alphaMask)
                        processedMask.Dispose();
                }
                else
                {
                    finalMask = processedMask;
                }
                
                if (alphaMask != processedMask && alphaMask != finalMask)
                    alphaMask.Dispose();
                
                linkedCt.ThrowIfCancellationRequested();
                
                // 5. Aplicar máscara con premultiplied alpha correcto (ApplyMask final)
                CrashLogger.Log("CAP_FINAL: APPLY_MASK_BEGIN");
                var swApply = System.Diagnostics.Stopwatch.StartNew();
                
                try
                {
                    cutoutBitmap = await System.Threading.Tasks.Task.Run(() =>
                    {
                        return _segmentationService.ApplyMask(originalBitmap, finalMask);
                    }, linkedCt);
                }
                catch (Exception exApply)
                {
                    CrashLogger.Log($"CAP_FINAL: APPLY_MASK_FAILED - {exApply.Message}", exApply);
                    // FALLBACK: Guardar foto original
                    try
                    {
                        var fallbackSessionPath3 = await _storageService.CreateSessionFolderAsync();
                        var fallbackTimestamp3 = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        fallbackPath = Path.Combine(fallbackSessionPath3, $"{fallbackTimestamp3}_original_fallback.png");
                        
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            using (var image = SkiaSharp.SKImage.FromBitmap(originalBitmap))
                            using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                            using (var stream = File.Create(fallbackPath))
                            {
                                data.SaveTo(stream);
                            }
                        }, linkedCt);
                        
                        CrashLogger.Log($"CAP_FINAL_FALLBACK - ApplyMask falló, guardada foto original: {fallbackPath}", exApply);
                        return (null, fallbackPath);
                    }
                    catch (Exception exFallback)
                    {
                        CrashLogger.Log($"CAP_FINAL: FALLBACK_FAILED - {exFallback.Message}", exFallback);
                        throw new Exception($"Error al aplicar máscara y fallback: {exApply.Message}", exApply);
                    }
                }
                
                swApply.Stop();
                CrashLogger.Log($"CAP_FINAL: APPLY_MASK_END - Time: {swApply.ElapsedMilliseconds}ms");
                
                if (cutoutBitmap == null || cutoutBitmap.IsNull)
                {
                    // FALLBACK: Guardar foto original
                    try
                    {
                        var fallbackSessionPath4 = await _storageService.CreateSessionFolderAsync();
                        var fallbackTimestamp4 = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        fallbackPath = Path.Combine(fallbackSessionPath4, $"{fallbackTimestamp4}_original_fallback.png");
                        
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            using (var image = SkiaSharp.SKImage.FromBitmap(originalBitmap))
                            using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                            using (var stream = File.Create(fallbackPath))
                            {
                                data.SaveTo(stream);
                            }
                        }, linkedCt);
                        
                        CrashLogger.Log($"CAP_FINAL_FALLBACK - CutoutBitmap null, guardada foto original: {fallbackPath}");
                        return (null, fallbackPath);
                    }
                    catch (Exception exFallback)
                    {
                        CrashLogger.Log($"CAP_FINAL: FALLBACK_FAILED - {exFallback.Message}", exFallback);
                        throw new Exception("No se pudo aplicar máscara y fallback falló");
                    }
                }
                
                linkedCt.ThrowIfCancellationRequested();
                
                // 6. Guardar foto cruda directamente en shotsDir (ruta fija determinística)
                CrashLogger.Log("CAP_FINAL: SAVE_RAW_BEGIN");
                var swSave = System.Diagnostics.Stopwatch.StartNew();
                string? rawPhotoPath = null;
                
                try
                {
                    rawPhotoPath = await _storageService.SaveRawPhotoAsync(originalBitmap);
                }
                catch (Exception exSaveRaw)
                {
                    CrashLogger.Log($"CAP_SAVE_FAIL: Error al guardar foto cruda - {exSaveRaw.Message}", exSaveRaw);
                    // Continuar de todas formas - no es crítico para el flujo
                }
                
                // 7. Guardar archivos intermedios en carpeta temporal (solo para procesamiento)
                var sessionPath = await _storageService.CreateSessionFolderAsync();
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                
                // Guardar alpha mask como PNG (Gray8/Alpha8) - solo para procesamiento interno
                var alphaPath = Path.Combine(sessionPath, $"{timestamp}_alpha.png");
                try
                {
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        alphaImage = SkiaSharp.SKImage.FromBitmap(finalMask);
                        alphaData = alphaImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                        using (var alphaStream = File.Create(alphaPath))
                        {
                            alphaData.SaveTo(alphaStream);
                        }
                    }, linkedCt);
                }
                catch (Exception exSaveAlpha)
                {
                    CrashLogger.Log($"CAP_FINAL: SAVE_ALPHA_FAILED - {exSaveAlpha.Message}", exSaveAlpha);
                    // No crítico - continuar
                }
                
                // Guardar cutout como PNG con alpha (RGBA) - solo para procesamiento interno
                var cutoutPath = Path.Combine(sessionPath, $"{timestamp}_cutout.png");
                try
                {
                    await System.Threading.Tasks.Task.Run(() =>
                    {
                        cutoutImage = SkiaSharp.SKImage.FromBitmap(cutoutBitmap);
                        cutoutData = cutoutImage.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                        using (var cutoutStream = File.Create(cutoutPath))
                        {
                            cutoutData.SaveTo(cutoutStream);
                        }
                    }, linkedCt);
                }
                catch (Exception exSaveCutout)
                {
                    CrashLogger.Log($"CAP_FINAL: SAVE_CUTOUT_FAILED - {exSaveCutout.Message}", exSaveCutout);
                    // No crítico - continuar
                }
                
                swSave.Stop();
                swTotal.Stop();
                swWatchdog.Stop();
                
                // Log memoria final
                GC.Collect(2, GCCollectionMode.Forced, false);
                var memAfter = GC.GetTotalMemory(false);
                process.Refresh();
                var workingSetAfter = process.WorkingSet64;
                var memDelta = memAfter - memBefore;
                var workingSetDelta = workingSetAfter - workingSetBefore;
                
                CrashLogger.Log($"CAP_FINAL: SAVE_END - Time: {swSave.ElapsedMilliseconds}ms, RawPhotoPath: {rawPhotoPath ?? "N/A"}, AlphaPath: {alphaPath}, CutoutPath: {cutoutPath}");
                CrashLogger.Log($"CAP_FINAL: SUCCESS - TotalTime: {swTotal.ElapsedMilliseconds}ms, MemAfter: {memAfter / 1024 / 1024}MB (Delta: {memDelta / 1024 / 1024}MB), WorkingSetAfter: {workingSetAfter / 1024 / 1024}MB (Delta: {workingSetDelta / 1024 / 1024}MB)");
                
                // Retornar paths: alphaPath y cutoutPath para compatibilidad con código existente
                // La foto cruda ya está guardada en shotsDir
                return (alphaPath, cutoutPath);
            }
            catch (OperationCanceledException)
            {
                swTotal.Stop();
                swWatchdog.Stop();
                CrashLogger.Log($"CAP_FINAL: CANCELLED - ThreadId: {threadId}, TotalTime: {swTotal.ElapsedMilliseconds}ms");
                return (null, null);
            }
            catch (Exception ex)
            {
                swTotal.Stop();
                swWatchdog.Stop();
                CrashLogger.Log($"CAP_FINAL: ERROR - ThreadId: {threadId}, Message: {ex.Message}, TotalTime: {swTotal.ElapsedMilliseconds}ms", ex);
                
                // Si hay fallback path, retornarlo
                if (!string.IsNullOrEmpty(fallbackPath))
                {
                    CrashLogger.Log($"CAP_FINAL: RETURNING_FALLBACK - {fallbackPath}");
                    return (null, fallbackPath);
                }
                
                return (null, null);
            }
            finally
            {
                // DISPOSE CORRECTO de todos los recursos (evitar dispose de referencias compartidas)
                try
                {
                    cutoutData?.Dispose();
                    cutoutImage?.Dispose();
                    alphaData?.Dispose();
                    alphaImage?.Dispose();
                    cutoutBitmap?.Dispose();
                    finalMask?.Dispose();
                    
                    // Dispose solo si no son referencias compartidas
                    if (processedMask != null && processedMask != alphaMask && processedMask != finalMask)
                        processedMask.Dispose();
                    if (alphaMask != null && alphaMask != processedMask && alphaMask != finalMask)
                        alphaMask.Dispose();
                    
                    originalBitmap?.Dispose();
                    convertedBitmap?.Dispose();
                    softwareBitmap?.Dispose();
                }
                catch (Exception exDispose)
                {
                    CrashLogger.Log($"CAP_FINAL: DISPOSE_ERROR - {exDispose.Message}", exDispose);
                }
                
                // SIEMPRE REANUDAR PREVIEW, incluso si hay excepción o cancelación
                if (_cameraService != null && previewWasRunning)
                {
                    try
                    {
                        await _cameraService.ResumePreviewAsync();
                    }
                    catch (Exception exResume)
                    {
                        CrashLogger.Log($"CAPTURE: Preview resume FAILED - {exResume.Message}", exResume);
                    }
                }
                
                // Liberar semáforo
                _captureFinalSemaphore.Release();
                
                // Limpiar CancellationTokenSource
                _captureFinalCts?.Dispose();
                _captureFinalCts = null;
            }
        }
        
        /// <summary>
        /// Helper: Convierte BitmapSource a SKBitmap
        /// </summary>
        private SkiaSharp.SKBitmap? BitmapSourceToSKBitmap(BitmapSource bitmapSource)
        {
            try
            {
                var width = bitmapSource.PixelWidth;
                var height = bitmapSource.PixelHeight;
                var stride = width * 4; // BGRA32
                var pixels = new byte[height * stride];
                
                bitmapSource.CopyPixels(pixels, stride, 0);
                
                var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                var bitmap = new SkiaSharp.SKBitmap(info);
                var ptr = bitmap.GetPixels();
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, ptr, pixels.Length);
                
                return bitmap;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("BITMAPSOURCE_TO_SKBITMAP: ERROR", ex);
                return null;
            }
        }
        
        /// <summary>
        /// Helper: Convierte SKBitmap a BitmapSource
        /// </summary>
        private BitmapSource? SKBitmapToBitmapSource(SkiaSharp.SKBitmap bitmap)
        {
            try
            {
                using (var image = SkiaSharp.SKImage.FromBitmap(bitmap))
                using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                {
                    var stream = new System.IO.MemoryStream();
                    data.SaveTo(stream);
                    stream.Position = 0;
                    
                    var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                    return decoder.Frames[0];
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("SKBITMAP_TO_BITMAPSOURCE: ERROR", ex);
                return null;
            }
        }
        
        private void ProcessFramePhase4(BitmapSource frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                // Phase4A: Solo copiar frame a buffer seguro
                if (_phase4SubPhase == Phase4SubPhase.Phase4A_MarshalOnly)
                {
                    ProcessPhase4A(frame, cancellationToken);
                    return;
                }
                
                // Phase4B: Convertir a SKBitmap (sin ONNX)
                if (_phase4SubPhase == Phase4SubPhase.Phase4B_SkiaConvertOnly)
                {
                    ProcessPhase4B(frame, cancellationToken);
                    return;
                }
                
                // Phase4C: Inferencia ONNX (sin post-proceso ni composición)
                if (_phase4SubPhase == Phase4SubPhase.Phase4C_ONNX_InferenceOnly)
                {
                    ProcessPhase4C(frame, cancellationToken);
                    return;
                }
                
                // Phase4D: Pipeline completo
                if (_phase4SubPhase == Phase4SubPhase.Phase4D_FullAlphaCompose)
                {
                    ProcessPhase4D(frame, cancellationToken);
                    return;
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"PROCESSFRAMEPHASE4: Error en subfase {_phase4SubPhase}", ex);
                throw; // Re-throw para que HandlePhase4Failure lo capture
            }
        }
        
        private void ProcessPhase4A(BitmapSource frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                // Copiar frame a WriteableBitmap seguro para UI
                var width = frame.PixelWidth;
                var height = frame.PixelHeight;
                var stride = width * 4; // BGRA32
                var pixels = new byte[height * stride];
                
                frame.CopyPixels(pixels, stride, 0);
                CrashLogger.Log("4A: FRAME_COPY_OK");
                
                // Crear WriteableBitmap y copiar datos
                var wb = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                wb.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                wb.Freeze();
                
                // FPS tracking
                _frameCount4A++;
                var elapsed = (DateTime.Now - _fpsStartTime).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    var fps = _frameCount4A / elapsed;
                    CrashLogger.Log($"4A: FPS={fps:F1}");
                    _frameCount4A = 0;
                    _fpsStartTime = DateTime.Now;
                }
                
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    PreviewFrameUpdated?.Invoke(this, wb);
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("4A: FRAME_COPY_FAILED", ex);
                throw;
            }
        }
        
        private void ProcessPhase4B(BitmapSource frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            SkiaSharp.SKBitmap? skBitmap = null;
            try
            {
                CrashLogger.Log("4B: SKBITMAP_CREATE_BEGIN");
                skBitmap = BitmapSourceToSKBitmap(frame);
                
                if (skBitmap == null || skBitmap.IsNull)
                {
                    CrashLogger.Log("4B: SKBITMAP_CREATE_FAILED (null)");
                    throw new InvalidOperationException("SKBitmap creation returned null");
                }
                
                CrashLogger.Log($"4B: SKBITMAP_CREATE_OK size={skBitmap.Width}x{skBitmap.Height}");
                
                // Test de estrés: crear y destruir cada frame
                cancellationToken.ThrowIfCancellationRequested();
                
                // Simular uso mínimo (solo verificar que es válido)
                var pixelCount = skBitmap.Width * skBitmap.Height;
                CrashLogger.Log($"4B: SKBITMAP_VALID pixelCount={pixelCount}");
                
                // Dispose inmediatamente (test de lifetime)
                skBitmap.Dispose();
                skBitmap = null;
                CrashLogger.Log("4B: SKBITMAP_DISPOSE_OK");
                
                // Volver a RAW para UI
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    PreviewFrameUpdated?.Invoke(this, frame);
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("4B: SKBITMAP_ERROR", ex);
                skBitmap?.Dispose();
                throw;
            }
        }
        
        private void ProcessPhase4C(BitmapSource frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (_backgroundSegmentationService == null || _segmentationService == null)
            {
                CrashLogger.Log("4C: SERVICES_NOT_INITIALIZED");
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    PreviewFrameUpdated?.Invoke(this, frame);
                }, DispatcherPriority.Render);
                return;
            }
            
            try
            {
                CrashLogger.Log("4C: ONNX_INFERENCE_BEGIN");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                
                // Solo inferencia ONNX, sin post-proceso ni composición
                var alphaMask = _backgroundSegmentationService.GenerateAlphaMatte(frame, targetWidth: 320);
                
                if (alphaMask == null || alphaMask.IsNull)
                {
                    CrashLogger.Log("4C: ONNX_FAILED (null mask)");
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        PreviewFrameUpdated?.Invoke(this, frame);
                    }, DispatcherPriority.Render);
                    return;
                }
                
                sw.Stop();
                CrashLogger.Log($"4C: ONNX_OK shape={alphaMask.Width}x{alphaMask.Height} ms={sw.ElapsedMilliseconds}");
                
                // Dispose inmediatamente (no usar para composición)
                alphaMask.Dispose();
                
                // Volver a RAW para UI
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    PreviewFrameUpdated?.Invoke(this, frame);
                }, DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("4C: ONNX_ERROR", ex);
                throw;
            }
        }
        
        private void ProcessPhase4D(BitmapSource frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            if (_backgroundSegmentationService == null || _alphaPostProcessor == null || _backgroundComposer == null)
            {
                CrashLogger.Log("4D: SERVICES_NOT_INITIALIZED");
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    PreviewFrameUpdated?.Invoke(this, frame);
                }, DispatcherPriority.Render);
                return;
            }
            
            if (_backgroundBitmap == null || _backgroundBitmap.IsNull)
            {
                CrashLogger.Log("4D: BACKGROUND_NOT_LOADED");
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    PreviewFrameUpdated?.Invoke(this, frame);
                }, DispatcherPriority.Render);
                return;
            }
            
            SkiaSharp.SKBitmap? alphaMask = null;
            SkiaSharp.SKBitmap? processedMask = null;
            SkiaSharp.SKBitmap? frameBitmap = null;
            SkiaSharp.SKBitmap? bgResized = null;
            SkiaSharp.SKBitmap? composed = null;
            
            try
            {
                lock (_frameProcessingLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // 1. Generar máscara alpha
                    CrashLogger.Log("4D: MASK_BEGIN");
                    alphaMask = _backgroundSegmentationService.GenerateAlphaMatte(frame, targetWidth: 320);
                    if (alphaMask == null || alphaMask.IsNull)
                    {
                        CrashLogger.Log("4D: MASK_FAILED");
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            PreviewFrameUpdated?.Invoke(this, frame);
                        }, DispatcherPriority.Render);
                        return;
                    }
                    CrashLogger.Log("4D: MASK_OK");
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // 2. Postprocesar máscara
                    CrashLogger.Log("4D: POST_BEGIN");
                    processedMask = _alphaPostProcessor.ProcessForPreview(alphaMask);
                    if (processedMask == null || processedMask.IsNull)
                    {
                        CrashLogger.Log("4D: POST_FAILED");
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            PreviewFrameUpdated?.Invoke(this, frame);
                        }, DispatcherPriority.Render);
                        return;
                    }
                    CrashLogger.Log("4D: POST_OK");
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // 3. Convertir frame a SKBitmap
                    frameBitmap = BitmapSourceToSKBitmap(frame);
                    if (frameBitmap == null || frameBitmap.IsNull)
                    {
                        CrashLogger.Log("4D: FRAMEBITMAP_FAILED");
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            PreviewFrameUpdated?.Invoke(this, frame);
                        }, DispatcherPriority.Render);
                        return;
                    }
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // 4. Redimensionar background
                    bgResized = _backgroundBitmap.Resize(
                        new SkiaSharp.SKImageInfo(frameBitmap.Width, frameBitmap.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Opaque),
                        SkiaSharp.SKFilterQuality.High);
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // 5. Componer
                    CrashLogger.Log("4D: COMPOSE_BEGIN");
                    composed = _backgroundComposer.Compose(frameBitmap, bgResized, processedMask);
                    if (composed == null || composed.IsNull)
                    {
                        CrashLogger.Log("4D: COMPOSE_FAILED");
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            PreviewFrameUpdated?.Invoke(this, frame);
                        }, DispatcherPriority.Render);
                        return;
                    }
                    CrashLogger.Log("4D: COMPOSE_OK");
                    
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    // 6. Convertir a BitmapSource
                    var composedBitmapSource = _backgroundComposer.SKBitmapToBitmapSource(composed);
                    if (composedBitmapSource == null)
                    {
                        CrashLogger.Log("4D: UI_UPDATE_FAILED (null BitmapSource)");
                        Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            PreviewFrameUpdated?.Invoke(this, frame);
                        }, DispatcherPriority.Render);
                        return;
                    }
                    
                    CrashLogger.Log("4D: UI_UPDATE_OK");
                    
                    Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        PreviewFrameUpdated?.Invoke(this, composedBitmapSource);
                    }, DispatcherPriority.Render);
                }
            }
            catch (OperationCanceledException)
            {
                // Frame descartado, no es error
                return;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("4D: ERROR", ex);
                throw;
            }
            finally
            {
                // Limpiar recursos
                alphaMask?.Dispose();
                processedMask?.Dispose();
                frameBitmap?.Dispose();
                bgResized?.Dispose();
                composed?.Dispose();
            }
        }
        
        private void HandlePhase4Failure(string step, Exception ex)
        {
            _consecutiveFailures++;
            CrashLogger.Log($"PHASE4_FAILURE: Step={step}, ConsecutiveFailures={_consecutiveFailures}", ex);
            
            // Circuit breaker: 3 fallos consecutivos = deshabilitar Phase4
            if (_consecutiveFailures >= 3)
            {
                _phase4Disabled = true;
                CrashLogger.Log($"PHASE4_CIRCUIT_BREAKER: Deshabilitando Phase4 después de {_consecutiveFailures} fallos consecutivos");
                
                // Mostrar warning en UI
                Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    ErrorMessage = $"Phase4 deshabilitado después de {_consecutiveFailures} fallos. Mostrando preview RAW.";
                }, DispatcherPriority.Normal);
            }
        }
        
        private async Task<string> ProcessCaptureCAP0(CancellationToken cancellationToken)
        {
            CrashLogger.Log("CAP0: RAW_STILL_BEGIN");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Solo capturar frame STILL y convertirlo a SoftwareBitmap seguro
                var softwareBitmap = await _cameraService.CaptureRawStillAsync();
                
                if (softwareBitmap == null)
                {
                    CrashLogger.Log("CAP0: RAW_STILL_FAILED (null)");
                    throw new Exception("No se pudo capturar frame STILL");
                }
                
                CrashLogger.Log($"CAP0: RAW_STILL_OK size={softwareBitmap.PixelWidth}x{softwareBitmap.PixelHeight} format={softwareBitmap.BitmapPixelFormat}");
                
                // Convertir a formato seguro si es necesario
                if (softwareBitmap.BitmapPixelFormat != Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8)
                {
                    CrashLogger.Log("CAP0: CONVERT_BEGIN");
                    var converted = Windows.Graphics.Imaging.SoftwareBitmap.Convert(softwareBitmap, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8);
                    softwareBitmap.Dispose();
                    softwareBitmap = converted;
                    CrashLogger.Log("CAP0: CONVERT_OK");
                }
                
                CrashLogger.Log("CAP0: READY (SoftwareBitmap creado, no guardado)");
                
                // NO guardar, solo validar que se puede crear
                softwareBitmap.Dispose();
                
                // Retornar path vacío (no se guarda en CAP0)
                return "";
            }
            catch (Exception ex)
            {
                CrashLogger.Log("CAP0: ERROR", ex);
                throw;
            }
        }
        
        private async Task<string> ProcessCaptureCAP1(CancellationToken cancellationToken)
        {
            CrashLogger.Log("CAP1: SAVE_BEGIN");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Capturar y guardar a archivo (sin procesamiento)
                var photoPath = await _cameraService.CapturePhotoAsync();
                
                if (string.IsNullOrEmpty(photoPath))
                {
                    CrashLogger.Log("CAP1: SAVE_FAILED (path vacío)");
                    throw new Exception("No se pudo guardar la foto");
                }
                
                if (!File.Exists(photoPath))
                {
                    CrashLogger.Log($"CAP1: SAVE_FAILED (archivo no existe: {photoPath})");
                    throw new Exception($"Archivo no existe después de guardar: {photoPath}");
                }
                
                var fileInfo = new FileInfo(photoPath);
                CrashLogger.Log($"CAP1: SAVE_OK path={photoPath} size={fileInfo.Length} bytes");
                CrashLogger.Log("CAP1: READY");
                
                return photoPath;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("CAP1: ERROR", ex);
                throw;
            }
        }
        
        private async Task<string> ProcessCaptureCAP2(CancellationToken cancellationToken)
        {
            CrashLogger.Log("CAP2: LOAD_SKIA_BEGIN");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Primero capturar y guardar (CAP1)
                var photoPath = await ProcessCaptureCAP1(cancellationToken);
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Cargar foto guardada a SKBitmap
                CrashLogger.Log($"CAP2: SKBITMAP_LOAD_BEGIN path={photoPath}");
                SkiaSharp.SKBitmap? skBitmap = null;
                using (var stream = File.OpenRead(photoPath))
                {
                    skBitmap = SkiaSharp.SKBitmap.Decode(stream);
                }
                
                if (skBitmap == null || skBitmap.IsNull)
                {
                    CrashLogger.Log("CAP2: SKBITMAP_LOAD_FAILED (null)");
                    throw new Exception("No se pudo cargar foto a SKBitmap");
                }
                
                CrashLogger.Log($"CAP2: SKBITMAP_LOAD_OK size={skBitmap.Width}x{skBitmap.Height} colorType={skBitmap.ColorType}");
                
                // Validar que es válido
                var pixelCount = skBitmap.Width * skBitmap.Height;
                CrashLogger.Log($"CAP2: SKBITMAP_VALID pixelCount={pixelCount}");
                
                // Dispose inmediatamente (test de lifetime)
                skBitmap.Dispose();
                skBitmap = null;
                CrashLogger.Log("CAP2: SKBITMAP_DISPOSE_OK");
                CrashLogger.Log("CAP2: READY");
                
                return photoPath;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("CAP2: ERROR", ex);
                throw;
            }
        }
        
        private async Task<string> ProcessCaptureCAP3(CancellationToken cancellationToken)
        {
            CrashLogger.Log("CAP3: ONNX_BEGIN");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Primero capturar y cargar (CAP2)
                var photoPath = await ProcessCaptureCAP2(cancellationToken);
                
                if (_backgroundSegmentationService == null || _segmentationService == null)
                {
                    CrashLogger.Log("CAP3: SERVICES_NOT_INITIALIZED");
                    throw new Exception("Servicios de segmentación no inicializados");
                }
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Cargar foto a SKBitmap
                CrashLogger.Log("CAP3: LOAD_BITMAP_BEGIN");
                SkiaSharp.SKBitmap? originalBitmap = null;
                using (var stream = File.OpenRead(photoPath))
                {
                    originalBitmap = SkiaSharp.SKBitmap.Decode(stream);
                }
                
                if (originalBitmap == null || originalBitmap.IsNull)
                {
                    CrashLogger.Log("CAP3: LOAD_BITMAP_FAILED");
                    throw new Exception("No se pudo cargar foto a SKBitmap");
                }
                
                CrashLogger.Log($"CAP3: LOAD_BITMAP_OK size={originalBitmap.Width}x{originalBitmap.Height}");
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Correr inferencia ONNX (sin post-proceso ni composición)
                CrashLogger.Log("CAP3: ONNX_INFERENCE_BEGIN");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var alphaMask = _backgroundSegmentationService.GenerateAlphaMatteFromBitmap(originalBitmap, targetWidth: 720);
                sw.Stop();
                
                if (alphaMask == null || alphaMask.IsNull)
                {
                    originalBitmap.Dispose();
                    CrashLogger.Log("CAP3: ONNX_INFERENCE_FAILED (null mask)");
                    throw new Exception("No se pudo generar máscara alpha");
                }
                
                CrashLogger.Log($"CAP3: ONNX_INFERENCE_OK shape={alphaMask.Width}x{alphaMask.Height} ms={sw.ElapsedMilliseconds}");
                
                // Dispose inmediatamente (no usar para post-proceso)
                alphaMask.Dispose();
                originalBitmap.Dispose();
                
                CrashLogger.Log("CAP3: READY");
                return photoPath;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("CAP3: ERROR", ex);
                throw;
            }
        }
        
        private async Task<string> ProcessCaptureCAP4(CancellationToken cancellationToken)
        {
            CrashLogger.Log("CAP4: FULL_PIPELINE_BEGIN");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Capturar still frame
                CrashLogger.Log("CAP4: CAPTURE_BEGIN");
                var stillFramePath = await _cameraService.CapturePhotoAsync();
                if (string.IsNullOrEmpty(stillFramePath))
                {
                    CrashLogger.Log("CAP4: CAPTURE_FAILED");
                    throw new Exception("No se pudo capturar la foto");
                }
                CrashLogger.Log("CAP4: CAPTURE_OK");
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Cargar BitmapSource desde archivo
                CrashLogger.Log("CAP4: LOAD_BITMAPSOURCE_BEGIN");
                BitmapSource? stillFrame = null;
                try
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.UriSource = new Uri(stillFramePath);
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    stillFrame = bitmapImage;
                }
                catch (Exception exLoad)
                {
                    CrashLogger.Log($"CAP4: LOAD_BITMAPSOURCE_FAILED - {exLoad.Message}", exLoad);
                    throw new Exception("No se pudo cargar la foto capturada", exLoad);
                }
                if (stillFrame == null)
                {
                    CrashLogger.Log("CAP4: LOAD_BITMAPSOURCE_FAILED - stillFrame es null");
                    throw new Exception("No se pudo cargar la foto capturada");
                }
                CrashLogger.Log("CAP4: LOAD_BITMAPSOURCE_OK");
                
                // Guardar foto original
                var sessionPath = await _storageService.CreateSessionFolderAsync();
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var originalPath = Path.Combine(sessionPath, $"original_{timestamp}.jpg");
                CrashLogger.Log($"CAP4: SAVE_ORIGINAL_BEGIN - {originalPath}");
                
                var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
                using (var stream = File.Create(originalPath))
                {
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(stillFrame));
                    encoder.Save(stream);
                }
                CrashLogger.Log("CAP4: SAVE_ORIGINAL_OK");
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // USAR PIPELINE FINAL: Procesamiento completo en background thread (NO bloquea preview)
                CrashLogger.Log("CAP4: PIPELINE_FINAL_BEGIN");
                
                // Marcar que el pipeline final está corriendo (preview seguirá funcionando)
                lock (_finalProcessingLock)
                {
                    _isFinalProcessingRunning = true;
                    
                    // Cancelar procesamiento anterior si existe
                    _currentFinalProcessingCts?.Cancel();
                    _currentFinalProcessingCts?.Dispose();
                    
                    // Crear nuevo cancellation token
                    _currentFinalProcessingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                }
                
                // Ejecutar pipeline final en background thread (NO bloquea preview)
                // El preview seguirá funcionando independientemente
                (string? alphaPath, string? cutoutPath) result;
                result = await Task.Run(async () =>
                {
                    try
                    {
                        return await ProcessFrameForFinal(stillFrame, sessionPath, _currentFinalProcessingCts.Token);
                    }
                    finally
                    {
                        // Marcar que el pipeline final terminó
                        lock (_finalProcessingLock)
                        {
                            _isFinalProcessingRunning = false;
                        }
                    }
                }, cancellationToken);
                var alphaPath = result.alphaPath;
                var cutoutPath = result.cutoutPath;
                
                if (string.IsNullOrEmpty(alphaPath) || string.IsNullOrEmpty(cutoutPath))
                {
                    CrashLogger.Log("CAP4: PIPELINE_FINAL_FAILED");
                    throw new Exception("No se pudo procesar la foto con pipeline final");
                }
                CrashLogger.Log($"CAP4: PIPELINE_FINAL_OK - alpha={alphaPath}, cutout={cutoutPath}");
                
                // Cargar alpha mask para extraer cabeza y cuello
                cancellationToken.ThrowIfCancellationRequested();
                CrashLogger.Log("CAP4: LOAD_ALPHA_BEGIN");
                var alphaBitmap = LoadAlphaMaskFromFile(alphaPath);
                if (alphaBitmap == null || alphaBitmap.IsNull)
                {
                    CrashLogger.Log("CAP4: LOAD_ALPHA_FAILED");
                    throw new Exception("No se pudo cargar máscara alpha");
                }
                CrashLogger.Log("CAP4: LOAD_ALPHA_OK");
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Cargar original como SKBitmap
                CrashLogger.Log("CAP4: LOAD_BITMAP_BEGIN");
                var originalBitmap = BitmapSourceToSKBitmap(stillFrame);
                if (originalBitmap == null || originalBitmap.IsNull)
                {
                    alphaBitmap.Dispose();
                    CrashLogger.Log("CAP4: LOAD_BITMAP_FAILED");
                    throw new Exception("No se pudo convertir a SKBitmap");
                }
                CrashLogger.Log($"CAP4: LOAD_BITMAP_OK size={originalBitmap.Width}x{originalBitmap.Height}");
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Extraer cabeza y cuello usando la máscara procesada
                CrashLogger.Log("CAP4: EXTRACT_HEAD_BEGIN");
                var headAndNeckBitmap = ExtractHeadAndNeckFromOriginal(originalBitmap, alphaBitmap);
                if (headAndNeckBitmap == null || headAndNeckBitmap.IsNull)
                {
                    alphaBitmap.Dispose();
                    originalBitmap.Dispose();
                    CrashLogger.Log("CAP4: EXTRACT_HEAD_FAILED");
                    throw new Exception("No se pudo extraer cabeza y cuello");
                }
                CrashLogger.Log($"CAP4: EXTRACT_HEAD_OK size={headAndNeckBitmap.Width}x{headAndNeckBitmap.Height}");
                
                // Limpiar (el collage se procesa después)
                alphaBitmap.Dispose();
                originalBitmap.Dispose();
                headAndNeckBitmap.Dispose();
                
                CrashLogger.Log("CAP4: READY");
                return originalPath;
            }
            catch (Exception ex)
            {
                CrashLogger.Log("CAP4: ERROR", ex);
                throw;
            }
        }
        
        /// <summary>
        /// Helper: Carga máscara alpha desde archivo PNG
        /// </summary>
        private SkiaSharp.SKBitmap? LoadAlphaMaskFromFile(string alphaPath)
        {
            try
            {
                using (var stream = File.OpenRead(alphaPath))
                using (var data = SkiaSharp.SKData.Create(stream))
                {
                    var image = SkiaSharp.SKImage.FromEncodedData(data);
                    if (image == null) return null;
                    
                    var bitmap = SkiaSharp.SKBitmap.FromImage(image);
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("LOAD_ALPHA_MASK: ERROR", ex);
                return null;
            }
        }
        
        private async Task ProcessCollageCAP4(string photoPath, CancellationToken cancellationToken)
        {
            CrashLogger.Log("CAP4_COLLAGE: BEGIN");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var sessionPath = await _storageService.CreateSessionFolderAsync();
                CrashLogger.Log($"CAP4_COLLAGE: SESSION_PATH={sessionPath}");
                
                // Buscar el background de figurita
                CrashLogger.Log("CAP4_COLLAGE: FIND_BACKGROUND_BEGIN");
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var possibleBackgroundPaths = new[]
                {
                    Path.Combine(desktop, "KCMundial", "Assets", "frames", "arg.png"),
                    Path.Combine(desktop, "KCMundial", "Assets", "background.png"),
                    Path.Combine(desktop, "KCMundial", "Assets", "figurita.png"),
                    Path.Combine(desktop, "KCMundial", "Assets", "sticker.png"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "frames", "arg.png"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "background.png"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "figurita.png"),
                    Path.Combine(AppContext.BaseDirectory, "Assets", "sticker.png")
                };
                
                string? backgroundPath = null;
                foreach (var path in possibleBackgroundPaths)
                {
                    if (File.Exists(path))
                    {
                        backgroundPath = path;
                        break;
                    }
                }
                CrashLogger.Log($"CAP4_COLLAGE: FIND_BACKGROUND_OK path={backgroundPath ?? "null"}");
                
                cancellationToken.ThrowIfCancellationRequested();
                
                // Procesar foto con segmentación
                CrashLogger.Log("CAP4_COLLAGE: PROCESS_PHOTO_BEGIN");
                string originalPhotoPath = _capturedPhotos[0];
                
                if (_backgroundSegmentationService == null || _alphaPostProcessor == null)
                {
                    CrashLogger.Log("CAP4_COLLAGE: SERVICES_NOT_INITIALIZED");
                    throw new Exception("Servicios de segmentación no inicializados");
                }

                // 1. Cargar frame original
                CrashLogger.Log("CAP4_COLLAGE: LOAD_BITMAP_BEGIN");
                SkiaSharp.SKBitmap? originalBitmap = null;
                using (var stream = File.OpenRead(originalPhotoPath))
                {
                    originalBitmap = SkiaSharp.SKBitmap.Decode(stream);
                }
                if (originalBitmap == null || originalBitmap.IsNull)
                {
                    CrashLogger.Log("CAP4_COLLAGE: LOAD_BITMAP_FAILED");
                    throw new Exception("No se pudo cargar la foto original");
                }
                CrashLogger.Log($"CAP4_COLLAGE: LOAD_BITMAP_OK size={originalBitmap.Width}x{originalBitmap.Height}");

                cancellationToken.ThrowIfCancellationRequested();

                // 2. Generar máscara alpha
                CrashLogger.Log("CAP4_COLLAGE: MASK_BEGIN");
                var alphaMask = _backgroundSegmentationService.GenerateAlphaMatteFromBitmap(originalBitmap, targetWidth: 720);
                if (alphaMask == null || alphaMask.IsNull)
                {
                    originalBitmap.Dispose();
                    CrashLogger.Log("CAP4_COLLAGE: MASK_FAILED");
                    throw new Exception("No se pudo generar máscara alpha");
                }
                CrashLogger.Log($"CAP4_COLLAGE: MASK_OK size={alphaMask.Width}x{alphaMask.Height}");

                cancellationToken.ThrowIfCancellationRequested();

                // 3. Postprocesar máscara
                CrashLogger.Log("CAP4_COLLAGE: POST_BEGIN");
                var processedMask = _alphaPostProcessor.ProcessForFinal(alphaMask);
                if (processedMask == null || processedMask.IsNull)
                {
                    alphaMask.Dispose();
                    originalBitmap.Dispose();
                    CrashLogger.Log("CAP4_COLLAGE: POST_FAILED");
                    throw new Exception("No se pudo postprocesar máscara");
                }
                CrashLogger.Log("CAP4_COLLAGE: POST_OK");

                cancellationToken.ThrowIfCancellationRequested();

                // 4. Extraer busto estilo Panini (nuevo método)
                CrashLogger.Log("CAP4_COLLAGE: EXTRACT_PANINI_BUST_BEGIN");
                var paniniBustBitmap = ExtractPaniniBust(originalBitmap, processedMask);
                if (paniniBustBitmap == null || paniniBustBitmap.IsNull)
                {
                    alphaMask.Dispose();
                    processedMask.Dispose();
                    originalBitmap.Dispose();
                    CrashLogger.Log("CAP4_COLLAGE: EXTRACT_PANINI_BUST_FAILED");
                    throw new Exception("No se pudo extraer busto Panini del frame original");
                }
                CrashLogger.Log($"CAP4_COLLAGE: EXTRACT_PANINI_BUST_OK size={paniniBustBitmap.Width}x{paniniBustBitmap.Height}");

                cancellationToken.ThrowIfCancellationRequested();

                // 5. Crear collage directamente con busto Panini (sin guardar intermedio)
                cancellationToken.ThrowIfCancellationRequested();
                CrashLogger.Log("CAP4_COLLAGE: CREATE_STICKER_BEGIN");
                string stripPath = await _collageService.CreateStickerPaniniAsync(paniniBustBitmap, sessionPath, backgroundPath);
                
                // Limpiar
                alphaMask.Dispose();
                processedMask.Dispose();
                originalBitmap.Dispose();
                paniniBustBitmap.Dispose();
                CrashLogger.Log($"CAP4_COLLAGE: CREATE_STICKER_OK path={stripPath}");
                
                if (string.IsNullOrEmpty(stripPath) || !System.IO.File.Exists(stripPath))
                {
                    CrashLogger.Log("CAP4_COLLAGE: CREATE_STICKER_VERIFY_FAILED");
                    throw new Exception("Error al generar el collage");
                }
                
                cancellationToken.ThrowIfCancellationRequested();

                // 7. Guardar strip
                CrashLogger.Log("CAP4_COLLAGE: SAVE_STRIP_BEGIN");
                var savedStripPath = await _storageService.SaveStripAsync(stripPath, sessionPath);
                CrashLogger.Log($"CAP4_COLLAGE: SAVE_STRIP_OK path={savedStripPath}");
                
                cancellationToken.ThrowIfCancellationRequested();

                // 8. Guardar fotos individuales
                CrashLogger.Log("CAP4_COLLAGE: SAVE_SHOTS_BEGIN");
                var savedShots = await _storageService.SaveShotsAsync(_capturedPhotos, sessionPath);
                CrashLogger.Log($"CAP4_COLLAGE: SAVE_SHOTS_OK count={savedShots.Count}");
                
                cancellationToken.ThrowIfCancellationRequested();

                // 9. Guardar metadata
                CrashLogger.Log("CAP4_COLLAGE: SAVE_METADATA_BEGIN");
                await _storageService.SaveMetadataAsync(sessionPath, _capturedPhotos, savedStripPath, savedShots);
                CrashLogger.Log("CAP4_COLLAGE: SAVE_METADATA_OK");

                // Verificar que se guardó correctamente
                if (System.IO.File.Exists(savedStripPath))
                {
                    CrashLogger.Log($"CAP4_COLLAGE: VERIFY_OK path={savedStripPath}");
                }
                else
                {
                    CrashLogger.Log($"CAP4_COLLAGE: VERIFY_FAILED path={savedStripPath}");
                }

                // Volver a Idle
                ResultStripPath = savedStripPath;
                ResetToIdle();
                CrashLogger.Log("CAP4_COLLAGE: READY");
            }
            catch (Exception ex)
            {
                CrashLogger.Log("CAP4_COLLAGE: ERROR", ex);
                throw;
            }
        }
        

        private void PlaySound(string soundName)
        {
            // Ejecutar en thread separado para no bloquear
            Task.Run(() =>
            {
                try
                {
                    // Intentar múltiples rutas posibles
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    var possiblePaths = new[]
                    {
                        Path.Combine(desktop, "KCMundial", "Assets", "sounds"),
                        Path.Combine(desktop, "KCMundial", "Assets", "sounds"),
                        Path.Combine(AppContext.BaseDirectory, "Assets", "sounds")
                    };
                    
                    string? soundFile = null;
                    bool isWav = false;
                    
                    // Buscar el archivo en todas las rutas posibles
                    foreach (var soundsPath in possiblePaths)
                    {
                        var wavFile = Path.Combine(soundsPath, $"{soundName}.wav");
                        var mp3File = Path.Combine(soundsPath, $"{soundName}.mp3");
                        
                        if (File.Exists(wavFile))
                        {
                            soundFile = wavFile;
                            isWav = true;
                            break;
                        }
                        else if (File.Exists(mp3File))
                        {
                            soundFile = mp3File;
                            isWav = false;
                            break;
                        }
                    }
                    
                    if (soundFile != null && File.Exists(soundFile))
                    {
                        if (isWav)
                        {
                            // Para .wav usar SoundPlayer con Play() (no bloquea)
                            try
                            {
                                var player = new SoundPlayer(soundFile);
                                player.Play(); // Play() no bloquea, PlaySync() sí
                                System.Diagnostics.Debug.WriteLine($"✓ Sonido WAV reproducido: {soundFile}");
                            }
                            catch (Exception exWav)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error al reproducir WAV: {exWav.Message}");
                            }
                        }
                        else
                        {
                            // Para .mp3 usar MediaPlayer en el Dispatcher con mejor manejo
                            Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                try
                                {
                                    var mediaPlayer = new MediaPlayer();
                                    mediaPlayer.Volume = 1.0;
                                    
                                    // Configurar eventos antes de abrir
                                    mediaPlayer.MediaOpened += (s, e) =>
                                    {
                                        // Reproducir cuando el archivo esté listo
                                        mediaPlayer.Play();
                                        System.Diagnostics.Debug.WriteLine($"✓ Sonido MP3 iniciado: {soundFile}");
                                    };
                                    
                                    mediaPlayer.MediaEnded += (s, e) =>
                                    {
                                        mediaPlayer.Close();
                                        mediaPlayer = null;
                                    };
                                    
                                    mediaPlayer.MediaFailed += (s, e) =>
                                    {
                                        System.Diagnostics.Debug.WriteLine($"✗ Error al cargar MP3: {e.ErrorException?.Message ?? "Error desconocido"}");
                                        mediaPlayer.Close();
                                        mediaPlayer = null;
                                    };
                                    
                                    // Abrir el archivo
                                    var uri = new Uri(soundFile, UriKind.Absolute);
                                    mediaPlayer.Open(uri);
                                    System.Diagnostics.Debug.WriteLine($"✓ Archivo MP3 abierto: {soundFile}");
                                }
                                catch (Exception ex2)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Error al reproducir MP3: {ex2.Message}");
                                    System.Diagnostics.Debug.WriteLine($"StackTrace: {ex2.StackTrace}");
                                }
                            }, System.Windows.Threading.DispatcherPriority.Normal);
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ Sonido no encontrado: {soundName}");
                        foreach (var path in possiblePaths)
                        {
                            System.Diagnostics.Debug.WriteLine($"  Buscado en: {path}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error al reproducir sonido '{soundName}': {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                }
            });
        }

        public void Cancel()
        {
            _cancellationTokenSource?.Cancel();
            ResetToIdle();
        }

        private async Task SavePhoto()
        {
            try
            {
                // Abrir carpeta donde se guardaron las fotos (ruta fija determinística)
                var stripsDir = StorageService.GetStripsDirectory();
                if (Directory.Exists(stripsDir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", stripsDir);
                }
                else
                {
                    // Si no existe, crear y abrir
                    Directory.CreateDirectory(stripsDir);
                    System.Diagnostics.Process.Start("explorer.exe", stripsDir);
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"CAP_SAVE_FAIL: Error al abrir carpeta - {ex.Message}", ex);
            }
        }

        private void ResetToIdle()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            // NO hacer StopPreview ni Dispose - mantener la cámara activa
            // Esto permite que la próxima sesión sea instantánea
            // _cameraService.StopPreview(); // COMENTADO - mantener activa
            // _cameraService.Dispose(); // COMENTADO - mantener activa

            CountdownText = "";
            ErrorMessage = "";
            ResultStripPath = "";
            _capturedPhotos.Clear();
            _currentPhotoIndex = 0;

            // IMPORTANTE: Cambiar a Idle PERO mantener la cámara activa
            // El preview seguirá enviando frames (gracias a OnFrameCaptured que ahora siempre envía)
            State = KCMundialState.Idle;
            
            // Asegurar que el preview esté activo (pero sin reinicializar)
            if (_cameraService.IsInitialized)
            {
                // La cámara ya está activa - NO hacer nada
                // Solo asegurar que siga enviando frames
            }
        }

        /// <summary>
        /// Extrae busto estilo Panini desde alphaMask de persona (nuevo método para evitar "cara gigante")
        /// </summary>
        private SkiaSharp.SKBitmap? ExtractPaniniBust(SkiaSharp.SKBitmap originalBitmap, SkiaSharp.SKBitmap alphaMask)
        {
            if (originalBitmap == null || originalBitmap.IsNull || alphaMask == null || alphaMask.IsNull)
            {
                CrashLogger.Log("ExtractPaniniBust: bitmap o máscara es null");
                return null;
            }

            try
            {
                // 1) Asegurar que máscara tenga el mismo tamaño que original
                SkiaSharp.SKBitmap? maskResized = null;
                if (alphaMask.Width != originalBitmap.Width || alphaMask.Height != originalBitmap.Height)
                {
                    var maskInfo = new SkiaSharp.SKImageInfo(originalBitmap.Width, originalBitmap.Height, SkiaSharp.SKColorType.Alpha8, SkiaSharp.SKAlphaType.Opaque);
                    maskResized = alphaMask.Resize(maskInfo, SkiaSharp.SKFilterQuality.Medium);
                }
                else
                {
                    maskResized = alphaMask.Copy();
                }

                // 2) Obtener bounding box de persona desde alphaMask
                int minX = originalBitmap.Width, minY = originalBitmap.Height;
                int maxX = 0, maxY = 0;
                bool foundPerson = false;

                unsafe
                {
                    var maskPtr = (byte*)maskResized.GetPixels();
                    var maskStride = maskResized.RowBytes;

                    for (int y = 0; y < maskResized.Height; y++)
                    {
                        for (int x = 0; x < maskResized.Width; x++)
                        {
                            byte alpha = maskPtr[y * maskStride + x];
                            if (alpha > 128) // Umbral 50%
                            {
                                foundPerson = true;
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                            }
                        }
                    }
                }

                if (!foundPerson)
                {
                    CrashLogger.Log("ExtractPaniniBust: No se encontró persona en la máscara");
                    if (maskResized != alphaMask)
                        maskResized?.Dispose();
                    return null;
                }

                // 3) Definir bust rect
                int personH = maxY - minY + 1;
                int personW = maxX - minX + 1;
                
                int top = minY + (int)(0.02 * personH);
                int bottom = minY + (int)(0.72 * personH); // busto (pecho medio)
                int left = minX - (int)(0.08 * personW);
                int right = maxX + (int)(0.08 * personW);
                
                // Clamp a límites
                if (top < 0) top = 0;
                if (left < 0) left = 0;
                if (bottom > originalBitmap.Height) bottom = originalBitmap.Height;
                if (right > originalBitmap.Width) right = originalBitmap.Width;
                
                int bustWidth = right - left;
                int bustHeight = bottom - top;
                
                CrashLogger.Log($"ExtractPaniniBust: Bounding box persona {personW}x{personH}, Bust rect {bustWidth}x{bustHeight} desde ({left}, {top})");

                // 4) Recortar original BGRA con ese rect y recortar alpha equivalente
                var bustBitmap = new SkiaSharp.SKBitmap(bustWidth, bustHeight, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
                
                unsafe
                {
                    var sourcePtr = (uint*)originalBitmap.GetPixels();
                    var maskPtr = (byte*)maskResized.GetPixels();
                    var destPtr = (uint*)bustBitmap.GetPixels();
                    var sourceStride = originalBitmap.RowBytes / 4;
                    var maskStride = maskResized.RowBytes;
                    var destStride = bustBitmap.RowBytes / 4;

                    for (int y = 0; y < bustHeight; y++)
                    {
                        for (int x = 0; x < bustWidth; x++)
                        {
                            int sourceX = left + x;
                            int sourceY = top + y;
                            
                            if (sourceX < originalBitmap.Width && sourceY < originalBitmap.Height)
                            {
                                uint sourcePixel = sourcePtr[sourceY * sourceStride + sourceX];
                                byte maskAlpha = maskPtr[sourceY * maskStride + sourceX];
                                
                                // Aplicar máscara al alpha
                                byte sourceA = (byte)((sourcePixel >> 24) & 0xFF);
                                byte finalAlpha = (byte)((sourceA * maskAlpha) / 255);
                                
                                uint finalPixel = (sourcePixel & 0x00FFFFFF) | ((uint)finalAlpha << 24);
                                destPtr[y * destStride + x] = finalPixel;
                            }
                        }
                    }
                }

                if (maskResized != alphaMask)
                    maskResized?.Dispose();

                CrashLogger.Log($"ExtractPaniniBust: Busto extraído {bustWidth}x{bustHeight}");
                return bustBitmap;
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"ExtractPaniniBust: Error - {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Extrae cabeza y cuello del frame original usando la máscara de persona
        /// REGLA: NUNCA usar frames ya compuestos, solo el frame original de la cámara
        /// </summary>
        private SkiaSharp.SKBitmap? ExtractHeadAndNeckFromOriginal(SkiaSharp.SKBitmap originalFrame, SkiaSharp.SKBitmap personMask)
        {
            if (originalFrame == null || originalFrame.IsNull || personMask == null || personMask.IsNull)
            {
                System.Diagnostics.Debug.WriteLine("✗ ExtractHeadAndNeckFromOriginal: frame o máscara es null");
                return null;
            }

            try
            {
                // Asegurar que frame y máscara tengan el mismo tamaño
                SkiaSharp.SKBitmap? maskResized = null;
                if (personMask.Width != originalFrame.Width || personMask.Height != originalFrame.Height)
                {
                    var maskInfo = new SkiaSharp.SKImageInfo(originalFrame.Width, originalFrame.Height, SkiaSharp.SKColorType.Alpha8, SkiaSharp.SKAlphaType.Opaque);
                    maskResized = personMask.Resize(maskInfo, SkiaSharp.SKFilterQuality.Medium);
                }
                else
                {
                    maskResized = personMask.Copy();
                }

                // Encontrar los límites de la persona usando la máscara
                int minX = originalFrame.Width, minY = originalFrame.Height;
                int maxX = 0, maxY = 0;
                bool foundPerson = false;

                unsafe
                {
                    var maskPtr = (byte*)maskResized.GetPixels();
                    var maskStride = maskResized.RowBytes;

                    for (int y = 0; y < maskResized.Height; y++)
                    {
                        for (int x = 0; x < maskResized.Width; x++)
                        {
                            byte alpha = maskPtr[y * maskStride + x];
                            
                            // Si el píxel tiene alpha suficiente, es parte de la persona
                            if (alpha > 128) // Umbral 50%
                            {
                                foundPerson = true;
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                            }
                        }
                    }
                }

                if (!foundPerson)
                {
                    System.Diagnostics.Debug.WriteLine("✗ No se encontró persona en la máscara");
                    if (maskResized != personMask)
                        maskResized?.Dispose();
                    return null;
                }

                int personWidth = maxX - minX + 1;
                int personHeight = maxY - minY + 1;
                System.Diagnostics.Debug.WriteLine($"Persona detectada: {personWidth}x{personHeight} en posición ({minX}, {minY})");

                // Cabeza y cuello es aproximadamente el 40% superior de la persona
                int headAndNeckHeight = (int)(personHeight * 0.40);
                int headAndNeckY = minY;
                int headAndNeckX = minX;
                int headAndNeckWidth = personWidth;

                // Asegurar que no exceda los límites
                if (headAndNeckY + headAndNeckHeight > originalFrame.Height)
                    headAndNeckHeight = originalFrame.Height - headAndNeckY;
                if (headAndNeckX + headAndNeckWidth > originalFrame.Width)
                    headAndNeckWidth = originalFrame.Width - headAndNeckX;

                System.Diagnostics.Debug.WriteLine($"Recortando cabeza y cuello: {headAndNeckWidth}x{headAndNeckHeight} desde ({headAndNeckX}, {headAndNeckY})");

                // Crear bitmap solo con la cabeza y cuello del frame original, aplicando la máscara
                var headAndNeckBitmap = new SkiaSharp.SKBitmap(headAndNeckWidth, headAndNeckHeight, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
                
                unsafe
                {
                    var sourcePtr = (uint*)originalFrame.GetPixels();
                    var maskPtr = (byte*)maskResized.GetPixels();
                    var destPtr = (uint*)headAndNeckBitmap.GetPixels();
                    var sourceStride = originalFrame.RowBytes / 4;
                    var maskStride = maskResized.RowBytes;
                    var destStride = headAndNeckBitmap.RowBytes / 4;

                    for (int y = 0; y < headAndNeckHeight; y++)
                    {
                        for (int x = 0; x < headAndNeckWidth; x++)
                        {
                            int sourceX = headAndNeckX + x;
                            int sourceY = headAndNeckY + y;
                            
                            if (sourceX < originalFrame.Width && sourceY < originalFrame.Height)
                            {
                                uint sourcePixel = sourcePtr[sourceY * sourceStride + sourceX];
                                byte maskAlpha = maskPtr[sourceY * maskStride + sourceX];
                                
                                // Aplicar máscara al alpha del píxel
                                byte sourceA = (byte)((sourcePixel >> 24) & 0xFF);
                                byte finalAlpha = (byte)((sourceA * maskAlpha) / 255);
                                
                                // Construir píxel con alpha modificado
                                uint finalPixel = (sourcePixel & 0x00FFFFFF) | ((uint)finalAlpha << 24);
                                destPtr[y * destStride + x] = finalPixel;
                            }
                        }
                    }
                }

                if (maskResized != personMask)
                    maskResized?.Dispose();

                System.Diagnostics.Debug.WriteLine($"✓✓✓ Cabeza y cuello extraídos del frame original: {headAndNeckWidth}x{headAndNeckHeight}");
                return headAndNeckBitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗✗✗ Error al extraer cabeza del frame original: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extrae solo la cabeza de un bitmap con fondo removido (método legacy - mantener para compatibilidad)
        /// </summary>
        private SkiaSharp.SKBitmap? ExtractHeadOnlyFromBitmap(SkiaSharp.SKBitmap personBitmap)
        {
            if (personBitmap == null || personBitmap.IsNull)
            {
                System.Diagnostics.Debug.WriteLine("✗ ExtractHeadOnlyFromBitmap: bitmap es null");
                return null;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"ExtractHeadOnlyFromBitmap: Procesando imagen {personBitmap.Width}x{personBitmap.Height}, AlphaType: {personBitmap.AlphaType}");
                
                // Verificar si la imagen tiene transparencia
                bool hasTransparency = personBitmap.AlphaType != SkiaSharp.SKAlphaType.Opaque;
                System.Diagnostics.Debug.WriteLine($"Imagen tiene transparencia: {hasTransparency}");

                // Encontrar los límites de la persona (área no transparente)
                int minX = personBitmap.Width, minY = personBitmap.Height;
                int maxX = 0, maxY = 0;
                bool foundPerson = false;
                int transparentPixels = 0;
                int opaquePixels = 0;

                unsafe
                {
                    var ptr = (uint*)personBitmap.GetPixels();
                    var stride = personBitmap.RowBytes / 4;

                    for (int y = 0; y < personBitmap.Height; y++)
                    {
                        for (int x = 0; x < personBitmap.Width; x++)
                        {
                            var pixel = ptr[y * stride + x];
                            var color = new SkiaSharp.SKColor((uint)pixel);
                            
                            // Si el píxel no es transparente (alpha > umbral), es parte de la persona
                            if (color.Alpha > 10) // Umbral bajo para detectar píxeles visibles
                            {
                                foundPerson = true;
                                opaquePixels++;
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                            }
                            else
                            {
                                transparentPixels++;
                            }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"Píxeles opacos: {opaquePixels}, Transparentes: {transparentPixels}");

                if (!foundPerson)
                {
                    // Si no hay transparencia, asumir que la persona está en el centro de la imagen
                    if (!hasTransparency)
                    {
                        System.Diagnostics.Debug.WriteLine("⚠ Imagen sin transparencia, usando área central para detectar persona");
                        // Usar el 60% central de la imagen (asumiendo que la persona está centrada)
                        int marginX = (int)(personBitmap.Width * 0.20); // 20% de margen a cada lado
                        int marginY = (int)(personBitmap.Height * 0.10); // 10% de margen arriba
                        
                        minX = marginX;
                        minY = marginY;
                        maxX = personBitmap.Width - marginX - 1;
                        maxY = personBitmap.Height - 1; // Hasta abajo
                        foundPerson = true;
                        
                        System.Diagnostics.Debug.WriteLine($"Área central usada: ({minX}, {minY}) a ({maxX}, {maxY})");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("✗ No se encontró persona en la imagen con transparencia");
                        return null;
                    }
                }

                int personWidth = maxX - minX + 1;
                int personHeight = maxY - minY + 1;
                System.Diagnostics.Debug.WriteLine($"Persona detectada: {personWidth}x{personHeight} en posición ({minX}, {minY})");

                // Cabeza y cuello es aproximadamente el 40% superior de la persona (cabeza completa + cuello)
                int headAndNeckHeight = (int)(personHeight * 0.40); // 40% desde arriba - CABEZA COMPLETA Y CUELLO
                int headAndNeckY = minY;
                int headAndNeckX = minX;
                int headAndNeckWidth = personWidth;

                // Asegurar que no exceda los límites
                if (headAndNeckY + headAndNeckHeight > personBitmap.Height)
                    headAndNeckHeight = personBitmap.Height - headAndNeckY;
                if (headAndNeckX + headAndNeckWidth > personBitmap.Width)
                    headAndNeckWidth = personBitmap.Width - headAndNeckX;

                System.Diagnostics.Debug.WriteLine($"Recortando cabeza y cuello: {headAndNeckWidth}x{headAndNeckHeight} desde ({headAndNeckX}, {headAndNeckY})");

                // Crear bitmap solo con la cabeza y cuello
                var headAndNeckBitmap = new SkiaSharp.SKBitmap(headAndNeckWidth, headAndNeckHeight, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul);
                
                unsafe
                {
                    var sourcePtr = (uint*)personBitmap.GetPixels();
                    var destPtr = (uint*)headAndNeckBitmap.GetPixels();
                    var sourceStride = personBitmap.RowBytes / 4;
                    var destStride = headAndNeckBitmap.RowBytes / 4;

                    for (int y = 0; y < headAndNeckHeight; y++)
                    {
                        for (int x = 0; x < headAndNeckWidth; x++)
                        {
                            int sourceX = headAndNeckX + x;
                            int sourceY = headAndNeckY + y;
                            
                            if (sourceX < personBitmap.Width && sourceY < personBitmap.Height)
                            {
                                destPtr[y * destStride + x] = sourcePtr[sourceY * sourceStride + sourceX];
                            }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"✓✓✓ Cabeza y cuello extraídos exitosamente: {headAndNeckWidth}x{headAndNeckHeight} desde ({headAndNeckX}, {headAndNeckY}) de persona {personWidth}x{personHeight}");
                return headAndNeckBitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗✗✗ Error al extraer cabeza: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Extrae cabeza y cuello de una foto (para luego escalar y rellenar el sticker)
        /// </summary>
        private async Task<string> ExtractHeadAndNeck(string photoPath, string outputFolder)
        {
            return await Task.Run(() =>
            {
                try
                {
                    var outputPath = Path.Combine(outputFolder, "photo_head_and_neck.png");
                    
                    using (var stream = File.OpenRead(photoPath))
                    {
                        using (var bitmap = SkiaSharp.SKBitmap.Decode(stream))
                        {
                            if (bitmap == null || bitmap.IsNull)
                            {
                                throw new Exception("No se pudo cargar la imagen");
                            }

                            // Extraer cabeza y cuello
                            System.Diagnostics.Debug.WriteLine($"=== INICIANDO EXTRACCIÓN DE CABEZA Y CUELLO ===");
                            System.Diagnostics.Debug.WriteLine($"Imagen original: {bitmap.Width}x{bitmap.Height}, AlphaType: {bitmap.AlphaType}");
                            
                            var headAndNeckBitmap = ExtractHeadOnlyFromBitmap(bitmap);
                            if (headAndNeckBitmap == null || headAndNeckBitmap.IsNull)
                            {
                                System.Diagnostics.Debug.WriteLine("✗✗✗ ERROR CRÍTICO: No se pudo extraer cabeza y cuello");
                                throw new Exception("No se pudo extraer la cabeza y cuello de la imagen.");
                            }
                            
                            System.Diagnostics.Debug.WriteLine($"✓ Cabeza y cuello extraídos: {headAndNeckBitmap.Width}x{headAndNeckBitmap.Height}");

                            // Guardar imagen de cabeza y cuello
                            using (var image = SkiaSharp.SKImage.FromBitmap(headAndNeckBitmap))
                            {
                                using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                                {
                                    using (var fileStream = File.Create(outputPath))
                                    {
                                        data.SaveTo(fileStream);
                                    }
                                }
                            }

                            headAndNeckBitmap.Dispose();
                            System.Diagnostics.Debug.WriteLine($"✓ Cabeza y cuello guardados: {outputPath}");
                            return outputPath;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"✗✗✗ ERROR en ExtractHeadAndNeck: {ex.Message}");
                    System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                    Services.LogService.Write($"✗✗✗ ERROR en ExtractHeadAndNeck: {ex.Message}");
                    // NO devolver la foto original - lanzar el error
                    throw new Exception($"Error al extraer cabeza y cuello: {ex.Message}", ex);
                }
            });
        }

        /// <summary>
        /// Procesa una foto eliminando el fondo usando IA (Remove.bg) o método básico como fallback
        /// </summary>
        private async Task<string> ProcessPhotoRemoveBackground(string photoPath, string outputFolder)
        {
            try
            {
                if (!File.Exists(photoPath))
                {
                    throw new FileNotFoundException($"Foto no encontrada: {photoPath}");
                }

                var outputPath = Path.Combine(outputFolder, "photo_no_bg.png");

                // INTENTO 1: Usar Remove.bg API (IA profesional) si está disponible
                // DESHABILITADO TEMPORALMENTE PARA NO GASTAR CRÉDITOS
                if (false && _removeBgService.IsAvailable)
                {
                    try
                    {
                        var msg1 = "Intentando eliminar fondo con Remove.bg API...";
                        System.Diagnostics.Debug.WriteLine(msg1);
                        Services.LogService.Write(msg1);
                        
                        var processedBitmap = await _removeBgService.RemoveBackgroundAsync(photoPath);
                        
                        if (processedBitmap != null && !processedBitmap.IsNull)
                        {
                            // Guardar imagen procesada
                            using (var image = SkiaSharp.SKImage.FromBitmap(processedBitmap))
                            {
                                using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                                {
                                    using (var fileStream = File.Create(outputPath))
                                    {
                                        data.SaveTo(fileStream);
                                    }
                                }
                            }
                            processedBitmap.Dispose();
                            var msg2 = "✓ Fondo eliminado exitosamente con Remove.bg API";
                            System.Diagnostics.Debug.WriteLine(msg2);
                            Services.LogService.Write(msg2);
                            return outputPath;
                        }
                    }
                    catch (Exception exRemoveBg)
                    {
                        var msg = $"⚠ Remove.bg API falló: {exRemoveBg.Message}. Usando método básico...";
                        System.Diagnostics.Debug.WriteLine(msg);
                        Services.LogService.Write(msg);
                    }
                }

                // INTENTO 2: Usar método básico como fallback
                var msg3 = "Usando método básico de eliminación de fondo...";
                System.Diagnostics.Debug.WriteLine(msg3);
                Services.LogService.Write(msg3);
                return await Task.Run(() =>
                {
                    // Cargar imagen original
                    using (var stream = File.OpenRead(photoPath))
                    {
                        using (var originalBitmap = SkiaSharp.SKBitmap.Decode(stream))
                        {
                            if (originalBitmap == null || originalBitmap.IsNull)
                            {
                                throw new Exception("No se pudo cargar la imagen");
                            }

                            // Eliminar fondo con método básico
                            var processedBitmap = _segmentationService.RemoveBackground(originalBitmap);
                            if (processedBitmap == null || processedBitmap.IsNull)
                            {
                                throw new Exception("No se pudo procesar la imagen");
                            }

                            // Guardar imagen procesada
                            using (var image = SkiaSharp.SKImage.FromBitmap(processedBitmap))
                            {
                                using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                                {
                                    using (var fileStream = File.Create(outputPath))
                                    {
                                        data.SaveTo(fileStream);
                                    }
                                }
                            }

                            processedBitmap.Dispose();
                            var msg4 = "✓ Fondo eliminado con método básico local";
                            System.Diagnostics.Debug.WriteLine(msg4);
                            Services.LogService.Write(msg4);
                            return outputPath;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error en ProcessPhotoRemoveBackground: {ex.Message}");
                throw;
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Func<Task>? _executeAsync;
        private readonly Action? _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
        {
            _executeAsync = executeAsync;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public async void Execute(object? parameter)
        {
            if (_executeAsync != null)
                await _executeAsync();
            else
                _execute?.Invoke();
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public class CameraInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string? DeviceId { get; set; }
        
        public override string ToString() => Name;
    }

    public class FrameInfo
    {
        public string FileName { get; set; } = "";
        public string FullPath { get; set; } = "";
        public System.Windows.Media.Imaging.BitmapImage? Thumbnail { get; set; }
    }

    public class DesignInfo
    {
        public PrintDesignType DesignType { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string FullPath { get; set; } = "";
        public System.Windows.Media.Imaging.BitmapImage? Thumbnail { get; set; }
    }

    public class BooleanToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
            return System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is System.Windows.Visibility visibility)
            {
                return visibility == System.Windows.Visibility.Visible;
            }
            return false;
        }
    }

    public class CountToVisibilityConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (value is int count)
            {
                return count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
            if (value is System.Collections.ICollection collection)
            {
                return collection.Count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }
            return System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

