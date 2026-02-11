using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using KCMundial.Services;
using Drawing = System.Drawing;
using System.Runtime.InteropServices;
using System.Reflection;

namespace KCMundial
{
    public partial class App : Application
    {
        // BUILD TAG único generado al iniciar (compile-time o runtime)
        public static readonly string BUILD_TAG = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        
        // FASE DE ARRANQUE - Cambiar aquí para aislar el crash
        // INICIALMENTE: Phase0_OnlyWindow (solo ventana vacía)
        public static readonly StartupPhase CURRENT_STARTUP_PHASE = StartupPhase.Phase4_FullPipeline;
        
        // SUBFASE DE Phase4 - Cambiar aquí para aislar el crash en procesamiento
        // INICIALMENTE: Phase4A_MarshalOnly (solo copiar frame)
        public static readonly Phase4SubPhase CURRENT_PHASE4_SUBPHASE = Phase4SubPhase.Phase4A_MarshalOnly;
        
        // SUBFASE DE CAPTURA - Cambiar aquí para aislar el crash en captura de foto
        // INICIALMENTE: CAP0_RawStillOnly (solo capturar frame STILL)
        public static readonly CaptureSubPhase CURRENT_CAPTURE_SUBPHASE = CaptureSubPhase.CAP0_RawStillOnly;
        
        private DispatcherTimer? _heartbeatTimer;
        private DispatcherTimer? _aliveTimer;
        private int _heartbeatCount = 0;

        // Constructor estático - se ejecuta ANTES de cualquier instancia
        static App()
        {
            // ESCRIBIR PROOF USANDO EL MISMO MÉTODO QUE LogService (que SÍ funciona)
            var buildTag = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string exePath = "UNKNOWN";
            int processId = 0;
            try
            {
                var p = System.Diagnostics.Process.GetCurrentProcess();
                exePath = p.MainModule?.FileName ?? "UNKNOWN";
                processId = p.Id;
            }
            catch { }
            
            // Usar EXACTAMENTE el mismo método que LogService
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var logFile = Path.Combine(desktop, "KCMundial", "Logs", $"app_{DateTime.Now:yyyyMMdd}.log");
                var logDir = Path.GetDirectoryName(logFile);
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }
                
                var proofPath = Path.Combine(logDir ?? desktop, $"PROOF_{buildTag}.txt");
                var content = $"[{DateTime.Now:HH:mm:ss.fff}] PROOF OK {buildTag} {processId} {exePath}\n";
                
                File.AppendAllText(proofPath, content, Encoding.UTF8);
            }
            catch { }
            
            // Inicializar logger lo antes posible
            var logPath = CrashLogger.Initialize();
            CrashLogger.Log($"STATIC_CONSTRUCTOR: App static constructor ejecutado. Log path: {logPath}");
        }


        // Constructor de instancia
        public App()
        {
            CrashLogger.Log("APP_CONSTRUCTOR: App constructor ejecutado");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Loggear BUILD tag y path del exe
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var exePath = process.MainModule?.FileName ?? "UNKNOWN";
            CrashLogger.Log($"ONSTARTUP_BEGIN: OnStartup iniciado. BUILD: {BUILD_TAG}, EXE: {exePath}");
            CrashLogger.Log($"ENTER PHASE {CURRENT_STARTUP_PHASE}");
            
            try
            {
                base.OnStartup(e);
                CrashLogger.Log("ONSTARTUP_BASE: base.OnStartup() completado");
            }
            catch (Exception ex)
            {
                CrashLogger.Log("ONSTARTUP_BASE_ERROR: Error en base.OnStartup()", ex);
                throw;
            }
            
            // Log inicial completo
            LogStartup();
            
            // Instrumentación GLOBAL de errores
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            
            // Handlers para identificar cierre normal vs crash
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                var stackTrace = Environment.StackTrace;
                var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                CrashLogger.Log($"CLOSE_PATH: AppDomain.ProcessExit llamado");
                CrashLogger.Log($"CLOSE_PATH: ThreadId={threadId}");
                CrashLogger.Log($"CLOSE_PATH: StackTrace=\n{stackTrace}");
            };
            
            this.SessionEnding += (s, e) =>
            {
                var stackTrace = Environment.StackTrace;
                var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                CrashLogger.Log($"CLOSE_PATH: SessionEnding llamado - Reason: {e.ReasonSessionEnding}");
                CrashLogger.Log($"CLOSE_PATH: ThreadId={threadId}");
                CrashLogger.Log($"CLOSE_PATH: StackTrace=\n{stackTrace}");
            };
            
            // Interceptar Application.Current.Exit
            this.Exit += App_Exit;
            
            // Interceptar Shutdown
            InterceptShutdown();
            
            CrashLogger.Log("ONSTARTUP_HANDLERS: Handlers de excepciones y cierre registrados");
        }
        
        /// <summary>
        /// Intercepta Application.Current.Shutdown() usando wrapper
        /// </summary>
        private void InterceptShutdown()
        {
            try
            {
                // Crear wrapper para Shutdown
                CrashLogger.Log("INTERCEPT: Shutdown wrapper registrado");
            }
            catch (Exception ex)
            {
                CrashLogger.Log("INTERCEPT: Error al interceptar Shutdown", ex);
            }
        }
        
        /// <summary>
        /// Wrapper para Application.Shutdown() que loguea antes de cerrar
        /// </summary>
        public new void Shutdown()
        {
            var stackTrace = Environment.StackTrace;
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            CrashLogger.Log($"CLOSE_PATH: Application.Shutdown() llamado");
            CrashLogger.Log($"CLOSE_PATH: ThreadId={threadId}");
            CrashLogger.Log($"CLOSE_PATH_STACK: {stackTrace}");
            base.Shutdown();
        }
        
        /// <summary>
        /// Wrapper para Application.Shutdown(int exitCode) que loguea antes de cerrar
        /// </summary>
        public new void Shutdown(int exitCode)
        {
            var stackTrace = Environment.StackTrace;
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            CrashLogger.Log($"CLOSE_PATH: Application.Shutdown({exitCode}) llamado");
            CrashLogger.Log($"CLOSE_PATH: ThreadId={threadId}");
            CrashLogger.Log($"CLOSE_PATH_STACK: {stackTrace}");
            base.Shutdown(exitCode);
        }
        
        /// <summary>
        /// Intercepta Environment.Exit() usando wrapper
        /// </summary>
        private void InterceptEnvironmentExit()
        {
            try
            {
                CrashLogger.Log("INTERCEPT: Environment.Exit wrapper registrado");
            }
            catch (Exception ex)
            {
                CrashLogger.Log("INTERCEPT: Error al interceptar Environment.Exit", ex);
            }
        }

        /// <summary>
        /// Muestra el estado del log en la UI (MainWindow)
        /// </summary>
        private void ShowLogStatusInUI()
        {
            try
            {
                var logPath = CrashLogger.GetLogPath();
                var lastError = CrashLogger.GetLastError();
                var logExists = CrashLogger.VerifyLogFile();
                
                var process = System.Diagnostics.Process.GetCurrentProcess();
                var exePath = process.MainModule?.FileName ?? "UNKNOWN";

                string statusMessage;
                if (string.IsNullOrEmpty(logPath))
                {
                    statusMessage = $"BUILD: {BUILD_TAG}\nLogging FAILED: No se pudo inicializar. Error: {lastError ?? "Unknown"}\nEXE: {exePath}";
                }
                else if (!string.IsNullOrEmpty(lastError))
                {
                    statusMessage = $"BUILD: {BUILD_TAG}\nLogging WARNING: {lastError}\nPath: {logPath}\nEXE: {exePath}";
                }
                else if (!logExists)
                {
                    statusMessage = $"BUILD: {BUILD_TAG}\nLogging WARNING: Archivo no existe aún\nPath: {logPath}\nEXE: {exePath}";
                }
                else
                {
                    statusMessage = $"BUILD: {BUILD_TAG}\nLogging OK: {logPath}\nEXE: {exePath}";
                }

                // Actualizar UI en el dispatcher
                this.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (MainWindow is MainWindow mainWin)
                        {
                            // Actualizar título de ventana con BUILD tag
                            mainWin.Title = $"KC Mundial (BUILD: {BUILD_TAG})";
                            
                            var logStatusText = mainWin.FindName("LogStatusText") as System.Windows.Controls.TextBlock;
                            if (logStatusText != null)
                            {
                                logStatusText.Text = statusMessage;
                                logStatusText.Visibility = Visibility.Visible; // Forzar visibilidad
                            }
                            else
                            {
                                // Si no existe el TextBlock, crear uno programáticamente
                                var fallbackText = new System.Windows.Controls.TextBlock
                                {
                                    Text = statusMessage,
                                    Foreground = new SolidColorBrush(Colors.Yellow),
                                    Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
                                    Padding = new Thickness(8, 4, 8, 4),
                                    FontSize = 12,
                                    TextWrapping = TextWrapping.Wrap,
                                    MaxWidth = 600,
                                    HorizontalAlignment = HorizontalAlignment.Left,
                                    VerticalAlignment = VerticalAlignment.Top,
                                    Margin = new Thickness(16, 16, 0, 0)
                                };
                                if (mainWin.Content is System.Windows.Controls.Grid grid)
                                {
                                    System.Windows.Controls.Panel.SetZIndex(fallbackText, 2000);
                                    grid.Children.Add(fallbackText);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        CrashLogger.Log("SHOW_LOG_STATUS_ERROR: Error al mostrar estado en UI", ex);
                    }
                }, DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("SHOW_LOG_STATUS_ERROR: Error al obtener estado del log", ex);
            }
        }

        /// <summary>
        /// Inicia el heartbeat timer para verificar que el log está activo
        /// </summary>
        private void StartHeartbeat()
        {
            try
            {
                CrashLogger.Log("BOOT OK");
                
                _heartbeatTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(500)
                };
                
                _heartbeatTimer.Tick += (s, e) =>
                {
                    _heartbeatCount++;
                    CrashLogger.Log($"HEARTBEAT {_heartbeatCount}");
                    
                    // Detener después de 3 heartbeats (1.5 segundos)
                    if (_heartbeatCount >= 3)
                    {
                        _heartbeatTimer?.Stop();
                    }
                };
                
                _heartbeatTimer.Start();
            }
            catch (Exception ex)
            {
                CrashLogger.Log("START_HEARTBEAT_ERROR: Error al iniciar heartbeat", ex);
            }
        }

        /// <summary>
        /// Inicia el timer de "ALIVE" para diagnóstico periódico (cada 1 segundo)
        /// </summary>
        private void StartAliveTimer()
        {
            try
            {
                _aliveTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                
                int tickCount = 0;
                _aliveTimer.Tick += (s, e) =>
                {
                    tickCount++;
                    CrashLogger.Log($"ALIVE_TICK {tickCount}");
                };
                
                _aliveTimer.Start();
                CrashLogger.Log("ALIVE_TIMER: Timer de diagnóstico iniciado (cada 1s)");
            }
            catch (Exception ex)
            {
                CrashLogger.Log("START_ALIVE_TIMER_ERROR: Error al iniciar alive timer", ex);
            }
        }

        private void LoadCustomFont()
        {
            try
            {
                // Usar Bauhaus 93
                FontFamily? fontFamily = null;
                var possibleNames = new[] { 
                    "Bauhaus 93",
                    "Bauhaus93",
                    "Segoe UI"
                };
                
                foreach (var name in possibleNames)
                {
                    try
                    {
                        fontFamily = new FontFamily(name);
                        break;
                    }
                    catch
                    {
                        continue;
                    }
                }
                
                if (fontFamily == null)
                {
                    Resources["TheSeasonsFont"] = new FontFamily("Segoe UI");
                }
                else
                {
                    Resources.Remove("TheSeasonsFont");
                    Resources["TheSeasonsFont"] = fontFamily;
                    
                    // Aplicar fuente directamente a la ventana principal si ya está cargada
                    if (MainWindow != null)
                    {
                        ApplyFontToWindow(MainWindow, fontFamily);
                    }
                }
            }
            catch (Exception ex)
            {
                CrashLogger.Log("LOAD_CUSTOM_FONT_ERROR: Error al cargar fuente", ex);
                Resources["TheSeasonsFont"] = new FontFamily("Segoe UI");
            }
        }

        /// <summary>
        /// Loguea inicio de aplicación
        /// </summary>
        private static void LogStartup()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("=".PadRight(80, '='));
                sb.AppendLine($"APPLICATION STARTUP: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine($"LOG PATH: {CrashLogger.GetLogPath()}");
                sb.AppendLine($"OS: {Environment.OSVersion}");
                sb.AppendLine($"CLR: {Environment.Version}");
                sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");
                sb.AppendLine($"CurrentDirectory: {Environment.CurrentDirectory}");
                sb.AppendLine($"WorkingSet: {Environment.WorkingSet / 1024 / 1024} MB");
                sb.AppendLine("=".PadRight(80, '='));
                
                CrashLogger.Log(sb.ToString());
            }
            catch (Exception ex)
            {
                CrashLogger.Log("LOGSTARTUP_ERROR: Error al loguear startup", ex);
            }
        }

        /// <summary>
        /// Maneja excepciones no capturadas en el Dispatcher (UI thread)
        /// CRÍTICO: NO cerrar la app automáticamente
        /// </summary>
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            CrashLogger.Log($"DISPATCHER_EXCEPTION: {e.Exception.GetType().Name} - {e.Exception.Message}", e.Exception);
            
            // NO cerrar la app - marcar como manejada
            e.Handled = true;
        }

        /// <summary>
        /// Maneja excepciones no capturadas en cualquier thread (managed)
        /// Puede ser fatal si IsTerminating = true
        /// </summary>
        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                var isTerminating = e.IsTerminating;
                CrashLogger.Log($"UNHANDLED_EXCEPTION: IsTerminating={isTerminating}, Type={ex.GetType().Name}, Message={ex.Message}", ex);
            }
            else
            {
                CrashLogger.Log($"UNHANDLED_NON_EXCEPTION: Type={e.ExceptionObject?.GetType().FullName ?? "null"}, IsTerminating={e.IsTerminating}");
            }
        }

        /// <summary>
        /// Maneja excepciones no observadas en Tasks (async/await)
        /// </summary>
        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            CrashLogger.Log($"TASK_EXCEPTION: {e.Exception.GetType().Name} - {e.Exception.Message}", e.Exception);
            
            // Marcar como observada para evitar que se propague
            e.SetObserved();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            var stackTrace = Environment.StackTrace;
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var reasonTag = "OnExit";
            
            CrashLogger.Log($"CLOSE_PATH: APP_EXIT - OnExit llamado");
            CrashLogger.Log($"CLOSE_PATH: ThreadId={threadId}, ReasonTag={reasonTag}, ApplicationCode={e.ApplicationExitCode}");
            CrashLogger.Log($"CLOSE_PATH_STACK: {stackTrace}");
            
            _heartbeatTimer?.Stop();
            _aliveTimer?.Stop();
            CrashLogger.Shutdown();
            base.OnExit(e);
        }
        
        /// <summary>
        /// Intercepta Application.Current.Exit
        /// </summary>
        private void App_Exit(object? sender, ExitEventArgs e)
        {
            var stackTrace = Environment.StackTrace;
            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            CrashLogger.Log($"CLOSE_PATH: Application.Current.Exit llamado");
            CrashLogger.Log($"CLOSE_PATH: ThreadId={threadId}, ExitCode={e.ApplicationExitCode}");
            CrashLogger.Log($"CLOSE_PATH: StackTrace=\n{stackTrace}");
        }

        private void ApplyFontToWindow(Window window, FontFamily fontFamily)
        {
            if (window == null || fontFamily == null) return;
            
            try
            {
                // Aplicar fuente a todos los TextBlocks y controles de texto
                ApplyFontToElement(window, fontFamily);
            }
            catch (Exception ex)
            {
                CrashLogger.Log("APPLY_FONT_TO_WINDOW_ERROR: Error al aplicar fuente a ventana", ex);
            }
        }

        private void ApplyFontToElement(DependencyObject element, FontFamily fontFamily)
        {
            if (element == null) return;
            
            try
            {
                // Aplicar a TextBlock
                if (element is System.Windows.Controls.TextBlock textBlock)
                {
                    textBlock.FontFamily = fontFamily;
                }
                // Aplicar a Button
                else if (element is System.Windows.Controls.Button button)
                {
                    button.FontFamily = fontFamily;
                }
                // Aplicar a otros controles de texto
                else if (element is System.Windows.Controls.Control control)
                {
                    control.FontFamily = fontFamily;
                }
                
                // Recursivamente aplicar a hijos
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(element, i);
                    ApplyFontToElement(child, fontFamily);
                }
            }
            catch
            {
                // Ignorar errores en elementos individuales
            }
        }
    }
}
