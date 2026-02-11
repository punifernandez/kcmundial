using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;
using KCMundial;

namespace KCMundial.Services
{
    public class CameraService : IDisposable
    {
        // POOL de cámaras - mantener todas pre-inicializadas (como Camera app de Windows)
        private Dictionary<string, MediaCapture> _cameraPool = new();
        private Dictionary<string, MediaFrameReader> _frameReaderPool = new();
        private MediaCapture? _activeMediaCapture;
        private MediaFrameReader? _activeFrameReader;
        private bool _isInitialized = false;
        private string? _selectedDeviceId;
        private WriteableBitmap? _previewBitmap;
        private Windows.Graphics.Imaging.SoftwareBitmap? _lastFrameBitmap;

        public event EventHandler<BitmapSource>? FrameCaptured;

        public bool IsInitialized => _isInitialized;
        
        private bool _isPreviewPaused = false;
        public bool IsPreviewPaused
        {
            get => _isPreviewPaused;
            private set
            {
                if (_isPreviewPaused != value)
                {
                    _isPreviewPaused = value;
                    CrashLogger.Log($"CameraService: IsPreviewPaused changed to {value}");
                }
            }
        }
        
        /// <summary>
        /// Obtiene el MediaCapture activo (para captura en máxima resolución)
        /// </summary>
        public MediaCapture? GetMediaCapture() => _activeMediaCapture;

        public static async Task<List<CameraInfo>> GetAvailableCamerasAsync()
        {
            var cameras = new List<CameraInfo>();
            try
            {
                var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
                int index = 0;
                
                // Filtrar cámaras virtuales (OBS, RDP, etc.) - como Camera app
                var virtualCameraKeywords = new[] { "OBS", "Virtual", "RDP", "Remote Desktop", "Snap Camera", "ManyCam", "XSplit" };
                
                foreach (var device in devices)
                {
                    // Filtrar cámaras virtuales por nombre
                    var isVirtual = virtualCameraKeywords.Any(keyword => 
                        device.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                    
                    if (!isVirtual)
                    {
                        cameras.Add(new CameraInfo 
                        { 
                            Index = index++, 
                            Name = device.Name,
                            DeviceId = device.Id
                        });
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Cámara virtual filtrada: {device.Name}");
                    }
                }
            }
            catch { }
            return cameras;
        }

        // Pre-inicializar TODAS las cámaras en el pool (como Camera app)
        // CRÍTICO: Debe ejecutarse en el UI thread (STA) - NO en Task.Run
        public async Task PreInitializeAllCamerasAsync(List<string> deviceIds)
        {
            // Inicializar SECUENCIALMENTE en el UI thread (STA)
            // NO usar Task.WhenAll - cada InitializeAsync debe completarse en STA
            foreach (var deviceId in deviceIds.Where(id => !_cameraPool.ContainsKey(id)))
            {
                try
                {
                    var mediaCapture = new MediaCapture();
                    var settings = new MediaCaptureInitializationSettings
                    {
                        VideoDeviceId = deviceId,
                        StreamingCaptureMode = StreamingCaptureMode.Video,
                        MediaCategory = MediaCategory.Other,
                        SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                        MemoryPreference = MediaCaptureMemoryPreference.Cpu
                    };

                    // Inicializar en el UI thread (STA) - CRÍTICO
                    await mediaCapture.InitializeAsync(settings);
                    _cameraPool[deviceId] = mediaCapture;
                    System.Diagnostics.Debug.WriteLine($"✓ Pre-inicializada cámara: {deviceId}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"✗ Error pre-inicializando {deviceId}: {ex.Message}");
                }
            }
        }

        public async Task<bool> InitializeAsync(string? deviceId = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                System.Diagnostics.Debug.WriteLine($"CameraService.InitializeAsync: Inicio - deviceId: {deviceId} ({sw.ElapsedMilliseconds} ms)");
                
                if (string.IsNullOrEmpty(deviceId))
                {
                    System.Diagnostics.Debug.WriteLine($"CameraService.InitializeAsync: deviceId vacío ({sw.ElapsedMilliseconds} ms)");
                        return false;
                    }

                // Si ya está en el pool, usar esa instancia (INSTANTÁNEO)
                if (_cameraPool.ContainsKey(deviceId))
                {
                    System.Diagnostics.Debug.WriteLine($"CameraService.InitializeAsync: Cámara en pool ({sw.ElapsedMilliseconds} ms)");
                    
                    // CRÍTICO: Detener el preview anterior ANTES de cambiar de cámara
                    await StopPreviewAsync();
                    
                    // Cambiar a la nueva cámara del pool (INSTANTÁNEO)
                    _activeMediaCapture = _cameraPool[deviceId];
                    _selectedDeviceId = deviceId;
                    _isInitialized = true;
                    System.Diagnostics.Debug.WriteLine($"CameraService.InitializeAsync: Cambiado a pool ({sw.ElapsedMilliseconds} ms)");
                    return true;
                }

                // Si no está en el pool, inicializar ahora (solo primera vez)
                // CRÍTICO: Esto debe ejecutarse en STA (UI thread)
                System.Diagnostics.Debug.WriteLine($"CameraService.InitializeAsync: Inicializando nueva cámara... ({sw.ElapsedMilliseconds} ms)");
                var result = await InitializeNewCameraAsync(deviceId);
                System.Diagnostics.Debug.WriteLine($"CameraService.InitializeAsync: Nueva cámara inicializada: {result} ({sw.ElapsedMilliseconds} ms)");
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CameraService.InitializeAsync: EXCEPCIÓN en {sw.ElapsedMilliseconds} ms");
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                _activeMediaCapture?.Dispose();
                _activeMediaCapture = null;
                _isInitialized = false;
                return false;
            }
        }
        
        private async Task<bool> InitializeNewCameraAsync(string deviceId)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                System.Diagnostics.Debug.WriteLine($"InitializeNewCameraAsync: Inicio - deviceId: {deviceId} ({sw.ElapsedMilliseconds} ms)");
                
                _selectedDeviceId = deviceId;
                _activeMediaCapture = new MediaCapture();
                
                // Settings mínimos - NO negociar formatos (esto acelera mucho)
                var settings = new MediaCaptureInitializationSettings
                {
                    VideoDeviceId = deviceId,
                    StreamingCaptureMode = StreamingCaptureMode.Video,
                    MediaCategory = MediaCategory.Other,
                    SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                    MemoryPreference = MediaCaptureMemoryPreference.Cpu
                };

                System.Diagnostics.Debug.WriteLine($"InitializeNewCameraAsync: Llamando InitializeAsync... ({sw.ElapsedMilliseconds} ms)");
                
                // CRÍTICO: InitializeAsync DEBE ejecutarse en STA (UI thread)
                // Si estamos aquí desde InitializeAsync que fue llamado desde UI thread, estamos bien
                await _activeMediaCapture.InitializeAsync(settings);
                
                System.Diagnostics.Debug.WriteLine($"InitializeNewCameraAsync: InitializeAsync completado ({sw.ElapsedMilliseconds} ms)");
                
                // Agregar al pool
                _cameraPool[deviceId] = _activeMediaCapture;
                _isInitialized = true;
                
                System.Diagnostics.Debug.WriteLine($"InitializeNewCameraAsync: COMPLETADO en {sw.ElapsedMilliseconds} ms");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InitializeNewCameraAsync: EXCEPCIÓN en {sw.ElapsedMilliseconds} ms");
                System.Diagnostics.Debug.WriteLine($"Error: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                _activeMediaCapture?.Dispose();
                _activeMediaCapture = null;
                _isInitialized = false;
                    return false;
            }
        }

        public async Task StopPreviewAsync()
        {
            // Detener el FrameReader activo para evitar que múltiples cámaras compitan
            if (_activeFrameReader != null)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("StopPreviewAsync: Deteniendo FrameReader anterior...");
                    _activeFrameReader.FrameArrived -= FrameReader_FrameArrived;
                    await _activeFrameReader.StopAsync();
                    _activeFrameReader.Dispose();
                    System.Diagnostics.Debug.WriteLine("StopPreviewAsync: FrameReader detenido correctamente");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"StopPreviewAsync: Error al detener FrameReader: {ex.Message}");
                }
                finally
                {
                    _activeFrameReader = null;
                }
            }
            
            // CRÍTICO: También detener cualquier FrameReader en el pool que pueda estar activo
            foreach (var reader in _frameReaderPool.Values)
            {
                try
                {
                    if (reader != _activeFrameReader)
                    {
                        reader.FrameArrived -= FrameReader_FrameArrived;
                        await reader.StopAsync();
                        reader.Dispose();
                        System.Diagnostics.Debug.WriteLine("StopPreviewAsync: FrameReader del pool detenido");
                    }
                }
                catch { }
            }
            _frameReaderPool.Clear();
            
            // Marcar preview como pausado
            IsPreviewPaused = true;
        }
        
        /// <summary>
        /// Pausa el preview de forma segura. Debe ser seguido por ResumePreviewAsync() en finally.
        /// </summary>
        public async Task PausePreviewAsync()
        {
            CrashLogger.Log("PREVIEW_PAUSE_BEGIN");
            try
            {
                await StopPreviewAsync();
                CrashLogger.Log("PREVIEW_PAUSE_OK");
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"PREVIEW_PAUSE_FAILED - {ex.Message}", ex);
                throw;
            }
        }
        
        /// <summary>
        /// Reanuda el preview. Debe llamarse en finally después de PausePreviewAsync().
        /// </summary>
        public async Task ResumePreviewAsync()
        {
            CrashLogger.Log("PREVIEW_RESUME_BEGIN");
            try
            {
                if (!_isInitialized || _activeMediaCapture == null)
                {
                    CrashLogger.Log("PREVIEW_RESUME_FAILED - Camera not initialized");
                    return;
                }
                
                // Re-suscribir el frame callback si se detuvo
                await StartPreviewAsync();
                CrashLogger.Log("PREVIEW_RESUME_OK");
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"PREVIEW_RESUME_FAILED - {ex.Message}", ex);
                // No lanzar excepción - el preview debe intentar reanudarse siempre
            }
        }

        public async Task StartPreviewAsync()
        {
            if (!_isInitialized || _activeMediaCapture == null)
            {
                System.Diagnostics.Debug.WriteLine("StartPreview: Cámara no inicializada");
                return;
            }

            // CRÍTICO: Detener preview anterior SIEMPRE antes de iniciar uno nuevo
            // Esto evita que múltiples cámaras envíen frames simultáneamente
            await StopPreviewAsync();

            try
            {
                System.Diagnostics.Debug.WriteLine("StartPreview: Iniciando preview...");
                var frameSource = _activeMediaCapture.FrameSources.Values.FirstOrDefault();
                if (frameSource != null && frameSource.Info.SourceKind == MediaFrameSourceKind.Color)
                {
                    // OPTIMIZACIÓN CRÍTICA: NO negociar formatos - usar el formato actual directamente
                    // Esto evita los 8 segundos de delay
                    var currentFormat = frameSource.CurrentFormat;
                    
                    // Crear FrameReader SIN especificar formato - usa el actual (más rápido)
                    // NO llamar SetFormatAsync - eso causa delay
                    _activeFrameReader = await _activeMediaCapture.CreateFrameReaderAsync(frameSource);
                    _activeFrameReader.FrameArrived += FrameReader_FrameArrived;
                    
                    // Iniciar INMEDIATAMENTE - no esperar nada
                    await _activeFrameReader.StartAsync();
                    System.Diagnostics.Debug.WriteLine($"✓ StartPreview: Preview iniciado - formato: {currentFormat.VideoFormat.Width}x{currentFormat.VideoFormat.Height}");
                    
                    // Marcar preview como activo (no pausado)
                    IsPreviewPaused = false;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("StartPreview: No se encontró frameSource, usando fallback");
                    StartPreviewFallback();
                    IsPreviewPaused = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"StartPreview: Error - {ex.Message}");
                StartPreviewFallback();
                IsPreviewPaused = false;
            }
        }
        
        /// <summary>
        /// Método legacy para compatibilidad - llama a StartPreviewAsync()
        /// </summary>
        public void StartPreview()
        {
            _ = StartPreviewAsync();
        }

        private void StartPreviewFallback()
        {
            if (!_isInitialized || _activeMediaCapture == null)
                return;

            _previewBitmap = new WriteableBitmap(640, 480, 96, 96, PixelFormats.Bgr32, null);

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            timer.Tick += async (s, e) =>
            {
                try
                {
                    var frame = await _activeMediaCapture!.GetPreviewFrameAsync();
                    if (frame?.SoftwareBitmap != null)
                    {
                        ProcessSoftwareBitmap(frame.SoftwareBitmap);
                        frame.Dispose();
                    }
                }
                catch { }
            };
            timer.Start();
        }

        private void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
        {
            // NO bloquear el thread del FrameReader - procesar de forma asíncrona
            _ = Task.Run(() =>
            {
                try
                {
                    var frame = sender.TryAcquireLatestFrame();
                    if (frame?.VideoMediaFrame?.SoftwareBitmap != null)
                    {
                        var bitmap = frame.VideoMediaFrame.SoftwareBitmap;
                        
                        // Guardar una copia del último frame para captura (en thread separado)
                        try
                        {
                            var oldBitmap = _lastFrameBitmap;
                            _lastFrameBitmap = Windows.Graphics.Imaging.SoftwareBitmap.Copy(bitmap);
                            oldBitmap?.Dispose();
                        }
                        catch (Exception exCopy)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error al copiar frame para captura: {exCopy.Message}");
                        }
                        
                        // Procesar en thread separado para no bloquear el FrameReader
                        ProcessSoftwareBitmap(bitmap);
                    }
                    frame?.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error en FrameReader_FrameArrived: {ex.Message}");
                }
            });
        }

        private void ProcessSoftwareBitmap(Windows.Graphics.Imaging.SoftwareBitmap bitmap)
        {
            // Procesar de forma asíncrona para no bloquear el FrameReader
            _ = Task.Run(async () =>
            {
                try
                {
                    await ProcessSoftwareBitmapSafe(bitmap);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error en ProcessSoftwareBitmap: {ex.Message}");
                }
            });
        }

        private async Task ProcessSoftwareBitmapSafe(Windows.Graphics.Imaging.SoftwareBitmap bitmap)
        {
            Windows.Graphics.Imaging.SoftwareBitmap? converted = null;
            try
            {
                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;

                converted = Windows.Graphics.Imaging.SoftwareBitmap.Convert(bitmap, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8);
                
                using var stream = new InMemoryRandomAccessStream();
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId, stream);
                encoder.SetSoftwareBitmap(converted);
                await encoder.FlushAsync();
                
                stream.Seek(0);
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var pixelData = await decoder.GetPixelDataAsync();
                var pixels = pixelData.DetachPixelData();

                // Usar InvokeAsync en lugar de Invoke para no bloquear
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (_previewBitmap == null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
                        {
                            _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                        }
                        
                        // NO hacer Freeze() - eso bloquea el bitmap y puede causar congelamiento
                        // En su lugar, crear un nuevo bitmap cada vez para fluidez
                        var newBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                        newBitmap.WritePixels(
                            new System.Windows.Int32Rect(0, 0, width, height),
                            pixels,
                            width * 4,
                            0);
                        newBitmap.Freeze();
                        
                        _previewBitmap = newBitmap;
                        FrameCaptured?.Invoke(this, _previewBitmap);
                    }
                    catch (Exception exUI)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error en UI thread: {exUI.Message}");
                    }
                }, System.Windows.Threading.DispatcherPriority.Render);
                
                converted?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error en ProcessSoftwareBitmapSafe: {ex.Message}");
                converted?.Dispose();
            }
        }

        public void StopPreview()
        {
            // NO detener el preview - mantenerlo activo
        }

        /// <summary>
        /// Captura un frame STILL y lo retorna como SoftwareBitmap (sin guardar a archivo).
        /// Usado para CAP0_RawStillOnly.
        /// </summary>
        public async Task<Windows.Graphics.Imaging.SoftwareBitmap?> CaptureRawStillAsync()
        {
            if (_activeMediaCapture == null || !_isInitialized)
            {
                System.Diagnostics.Debug.WriteLine("CaptureRawStillAsync: Cámara no está lista");
                return null;
            }

            try
            {
                System.Diagnostics.Debug.WriteLine("=== CaptureRawStillAsync: INICIO ===");
                Windows.Graphics.Imaging.SoftwareBitmap? softwareBitmap = null;
                
                // MÉTODO 1: Intentar usar el último frame del preview
                if (_lastFrameBitmap != null)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("CaptureRawStillAsync: Usando último frame del preview");
                        softwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap.Copy(_lastFrameBitmap);
                        System.Diagnostics.Debug.WriteLine($"✓ CaptureRawStillAsync: ÉXITO - Frame copiado");
                        return softwareBitmap;
                    }
                    catch (Exception exFrame)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ CaptureRawStillAsync: FALLO frame preview - {exFrame.Message}");
                    }
                }
                
                // MÉTODO 2: LowLagPhotoCapture
                try
                {
                    System.Diagnostics.Debug.WriteLine("CaptureRawStillAsync: Intentando LowLagPhotoCapture");
                    var lowLagCapture = await _activeMediaCapture.PrepareLowLagPhotoCaptureAsync(ImageEncodingProperties.CreateJpeg());
                    System.Diagnostics.Debug.WriteLine("CaptureRawStillAsync: LowLagPhotoCapture preparado");
                    
                    try
                    {
                        await Task.Delay(300);
                        var capturedPhoto = await lowLagCapture.CaptureAsync();
                        System.Diagnostics.Debug.WriteLine("CaptureRawStillAsync: Foto capturada con LowLagPhotoCapture");
                        
                        softwareBitmap = capturedPhoto.Frame.SoftwareBitmap;
                        if (softwareBitmap != null)
                        {
                            softwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap.Copy(softwareBitmap);
                            System.Diagnostics.Debug.WriteLine($"✓ CaptureRawStillAsync: ÉXITO - Frame copiado");
                            return softwareBitmap;
                        }
                    }
                    finally
                    {
                        await lowLagCapture.FinishAsync();
                    }
                }
                catch (Exception exLowLag)
                {
                    System.Diagnostics.Debug.WriteLine($"✗ CaptureRawStillAsync: FALLO LowLag - {exLowLag.Message}");
                }
                
                System.Diagnostics.Debug.WriteLine("=== CaptureRawStillAsync: FALLO TOTAL ===");
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CaptureRawStillAsync: EXCEPCIÓN - {ex.Message}");
                return null;
            }
        }

        public async Task<string> CapturePhotoAsync()
        {
            if (_activeMediaCapture == null || !_isInitialized)
            {
                System.Diagnostics.Debug.WriteLine("CapturePhotoAsync: Cámara no está lista");
                return "";
            }

            try
            {
                var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"photo_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jpg");
                System.Diagnostics.Debug.WriteLine($"=== CapturePhotoAsync: INICIO ===");
                System.Diagnostics.Debug.WriteLine($"Ruta destino: {tempPath}");
                System.Diagnostics.Debug.WriteLine($"_lastFrameBitmap es null: {_lastFrameBitmap == null}");
                System.Diagnostics.Debug.WriteLine($"_activeFrameReader es null: {_activeFrameReader == null}");
                
                Windows.Graphics.Imaging.SoftwareBitmap? softwareBitmap = null;
                string? metodoUsado = null;
                
                // MÉTODO 1: Intentar usar el último frame del preview (más rápido y confiable)
                if (_lastFrameBitmap != null)
                {
                    try
                    {
                        System.Diagnostics.Debug.WriteLine("MÉTODO 1: Intentando usar último frame del preview");
                        System.Diagnostics.Debug.WriteLine($"Formato: {_lastFrameBitmap.BitmapPixelFormat}, Tamaño: {_lastFrameBitmap.PixelWidth}x{_lastFrameBitmap.PixelHeight}");
                        softwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap.Copy(_lastFrameBitmap);
                        metodoUsado = "Último frame del preview";
                        System.Diagnostics.Debug.WriteLine($"✓ MÉTODO 1: ÉXITO - Frame copiado");
                    }
                    catch (Exception exFrame)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ MÉTODO 1: FALLO - {exFrame.Message}");
                        System.Diagnostics.Debug.WriteLine($"StackTrace: {exFrame.StackTrace}");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("MÉTODO 1: Omitido - _lastFrameBitmap es null");
                }
                
                // MÉTODO 2: Si no hay frame del preview, usar LowLagPhotoCapture
                if (softwareBitmap == null)
                {
                    System.Diagnostics.Debug.WriteLine("MÉTODO 2: Intentando LowLagPhotoCapture");
                    try
                    {
                        // NO detener el FrameReader - usar LowLagPhotoCapture en paralelo
                        // Esto evita que el preview se congele
                        var lowLagCapture = await _activeMediaCapture.PrepareLowLagPhotoCaptureAsync(ImageEncodingProperties.CreateJpeg());
                        System.Diagnostics.Debug.WriteLine("MÉTODO 2: LowLagPhotoCapture preparado");
                        
                        try
                        {
                            await Task.Delay(300); // Pequeño delay para estabilizar
                            var capturedPhoto = await lowLagCapture.CaptureAsync();
                            System.Diagnostics.Debug.WriteLine("MÉTODO 2: Foto capturada con LowLagPhotoCapture");
                            
                            softwareBitmap = capturedPhoto.Frame.SoftwareBitmap;
                            if (softwareBitmap != null)
                            {
                                softwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap.Copy(softwareBitmap);
                                metodoUsado = "LowLagPhotoCapture";
                                System.Diagnostics.Debug.WriteLine($"✓ MÉTODO 2: ÉXITO - Frame copiado");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine("✗ MÉTODO 2: SoftwareBitmap es null después de capturar");
                            }
                        }
                        finally
                        {
                            await lowLagCapture.FinishAsync();
                        }
                    }
                    catch (Exception exLowLag)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ MÉTODO 2: FALLO - {exLowLag.Message}");
                        System.Diagnostics.Debug.WriteLine($"StackTrace: {exLowLag.StackTrace}");
                        if (exLowLag.InnerException != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"InnerException: {exLowLag.InnerException.Message}");
                        }
                    }
                }
                
                // MÉTODO 3: Último recurso - GetPreviewFrameAsync
                if (softwareBitmap == null)
                {
                    System.Diagnostics.Debug.WriteLine("MÉTODO 3: Intentando GetPreviewFrameAsync");
                    try
                    {
                        var previewFrame = await _activeMediaCapture.GetPreviewFrameAsync();
                        if (previewFrame?.SoftwareBitmap != null)
                        {
                            softwareBitmap = Windows.Graphics.Imaging.SoftwareBitmap.Copy(previewFrame.SoftwareBitmap);
                            metodoUsado = "GetPreviewFrameAsync";
                            System.Diagnostics.Debug.WriteLine($"✓ MÉTODO 3: ÉXITO - Frame copiado");
                            previewFrame.Dispose();
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("✗ MÉTODO 3: previewFrame o SoftwareBitmap es null");
                        }
                    }
                    catch (Exception exPreview)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ MÉTODO 3: FALLO - {exPreview.Message}");
                        System.Diagnostics.Debug.WriteLine($"StackTrace: {exPreview.StackTrace}");
                    }
                }
                
                if (softwareBitmap != null)
                {
                    System.Diagnostics.Debug.WriteLine($"=== PROCESANDO SOFTWAREBITMAP ===");
                    System.Diagnostics.Debug.WriteLine($"Método usado: {metodoUsado}");
                    System.Diagnostics.Debug.WriteLine($"Tamaño original: {softwareBitmap.PixelWidth}x{softwareBitmap.PixelHeight}");
                    System.Diagnostics.Debug.WriteLine($"Formato original: {softwareBitmap.BitmapPixelFormat}");
                    
                    try
                    {
                        // Convertir a formato compatible si es necesario
                        if (softwareBitmap.BitmapPixelFormat != Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8)
                        {
                            System.Diagnostics.Debug.WriteLine("Convirtiendo a Bgra8...");
                            var converted = Windows.Graphics.Imaging.SoftwareBitmap.Convert(softwareBitmap, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8);
                            softwareBitmap.Dispose();
                            softwareBitmap = converted;
                            System.Diagnostics.Debug.WriteLine("✓ Conversión exitosa");
                        }
                        
                        // Guardar directamente desde SoftwareBitmap
                        System.Diagnostics.Debug.WriteLine("Creando encoder JPEG...");
                        using var stream = new InMemoryRandomAccessStream();
                        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, stream);
                        System.Diagnostics.Debug.WriteLine("Encoder creado, estableciendo SoftwareBitmap...");
                        encoder.SetSoftwareBitmap(softwareBitmap);
                        System.Diagnostics.Debug.WriteLine("Flush encoder...");
                        await encoder.FlushAsync();
                        System.Diagnostics.Debug.WriteLine("✓ Encoder flush exitoso");
                        
                        stream.Seek(0);
                        var buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
                        await stream.ReadAsync(buffer, (uint)stream.Size, InputStreamOptions.None);
                        
                        var data = new byte[buffer.Length];
                        using (var dataReader = DataReader.FromBuffer(buffer))
                        {
                            dataReader.ReadBytes(data);
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"Escribiendo archivo ({data.Length} bytes)...");
                        System.IO.File.WriteAllBytes(tempPath, data);
                        softwareBitmap.Dispose();
                        
                        var fileInfo = new System.IO.FileInfo(tempPath);
                        System.Diagnostics.Debug.WriteLine($"Archivo escrito: {fileInfo.Length} bytes");

                        if (fileInfo.Exists && fileInfo.Length > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"=== CapturePhotoAsync: ÉXITO ===");
                            System.Diagnostics.Debug.WriteLine($"Método: {metodoUsado}");
                            System.Diagnostics.Debug.WriteLine($"Archivo: {tempPath}");
                            return tempPath;
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("✗ Archivo no se creó correctamente o está vacío");
                        }
                    }
                    catch (Exception exProcess)
                    {
                        System.Diagnostics.Debug.WriteLine($"✗ ERROR AL PROCESAR SOFTWAREBITMAP: {exProcess.Message}");
                        System.Diagnostics.Debug.WriteLine($"StackTrace: {exProcess.StackTrace}");
                        if (exProcess.InnerException != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"InnerException: {exProcess.InnerException.Message}");
                        }
                        softwareBitmap?.Dispose();
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("✗✗✗ TODOS LOS MÉTODOS FALLARON - No se pudo obtener SoftwareBitmap");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"✗✗✗ EXCEPCIÓN GENERAL: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    System.Diagnostics.Debug.WriteLine($"InnerException: {ex.InnerException.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("=== CapturePhotoAsync: FALLO TOTAL ===");
            return "";
        }

        public void Dispose()
        {
            StopPreview();
            
            // NO hacer Dispose del pool - mantener las cámaras activas
            // Solo limpiar la referencia activa
            _activeFrameReader = null;
            _activeMediaCapture = null;
            _isInitialized = false;
            _previewBitmap = null;
        }
    }
}
