using System;
using System.IO;
using System.Text;
using System.Threading;

namespace KCMundial.Services
{
    /// <summary>
    /// Logger robusto con FileStream + StreamWriter con AutoFlush para garantizar escritura incluso si el proceso muere
    /// </summary>
    public static class CrashLogger
    {
        private static readonly object _lock = new object();
        private static FileStream? _fileStream;
        private static StreamWriter? _writer;
        private static string _logPath = "";
        private static bool _initialized = false;
        private static string? _lastError = null;

        /// <summary>
        /// Inicializa el logger. Debe llamarse lo antes posible.
        /// </summary>
        public static string Initialize()
        {
            lock (_lock)
            {
                if (_initialized && !string.IsNullOrEmpty(_logPath))
                {
                    return _logPath;
                }

                // Ruta principal: C:\KCMundial\Logs\KCMundial_crashlog.txt (ruta fija)
                string? primaryPath = null;
                try
                {
                    const string BasePath = @"C:\KCMundial";
                    var logDir = Path.Combine(BasePath, "Logs");
                    Directory.CreateDirectory(logDir);
                    primaryPath = Path.Combine(logDir, "KCMundial_crashlog.txt");
                }
                catch (Exception ex)
                {
                    _lastError = $"Failed to create fixed log path: {ex.Message}";
                }

                // Si falla, fallback a Temp
                if (string.IsNullOrEmpty(primaryPath))
                {
                    try
                    {
                        var tempPath = Path.GetTempPath();
                        primaryPath = Path.Combine(tempPath, "KCMundial_crashlog.txt");
                    }
                    catch (Exception ex)
                    {
                        _lastError = $"Failed to create TEMP path: {ex.Message}";
                        return "";
                    }
                }

                try
                {
                    // Abrir FileStream con FileMode.Append y FileShare.ReadWrite
                    _fileStream = new FileStream(primaryPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(_fileStream, Encoding.UTF8)
                    {
                        AutoFlush = true // CRÍTICO: flush automático
                    };

                    _logPath = primaryPath;
                    _initialized = true;
                    _lastError = null;

                    // Escribir header inicial
                    var separator = new string('=', 80);
                    var header = $"\n{separator}\n";
                    header += $"LOGGER INITIALIZED: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n";
                    header += $"LOG PATH: {_logPath}\n";
                    header += $"OS: {Environment.OSVersion}\n";
                    header += $"CLR: {Environment.Version}\n";
                    header += $"BaseDirectory: {AppContext.BaseDirectory}\n";
                    header += $"CurrentDirectory: {Environment.CurrentDirectory}\n";
                    header += $"{separator}\n";

                    _writer.Write(header);
                    _writer.Flush(); // Flush explícito

                    return _logPath;
                }
                catch (Exception ex)
                {
                    _lastError = $"Failed to open log file: {ex.Message}";
                    _fileStream?.Dispose();
                    _writer?.Dispose();
                    _fileStream = null;
                    _writer = null;
                    return "";
                }
            }
        }

        /// <summary>
        /// Escribe un mensaje al log con timestamp ISO y thread id
        /// </summary>
        public static void Log(string message, Exception? ex = null)
        {
            lock (_lock)
            {
                if (!_initialized || _writer == null)
                {
                    // Intentar inicializar si no está inicializado
                    Initialize();
                    if (!_initialized || _writer == null)
                    {
                        return; // Si falla, no hacer nada (evitar loops)
                    }
                }

                try
                {
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    var threadId = Thread.CurrentThread.ManagedThreadId;
                    var logLine = $"[{timestamp}] [TID:{threadId}] {message}";

                    if (ex != null)
                    {
                        logLine += $"\n  Exception: {ex.GetType().FullName}";
                        logLine += $"\n  Message: {ex.Message}";
                        if (!string.IsNullOrEmpty(ex.StackTrace))
                        {
                            logLine += $"\n  StackTrace:\n{ex.StackTrace}";
                        }
                        if (ex.InnerException != null)
                        {
                            logLine += $"\n  InnerException: {ex.InnerException.GetType().FullName} - {ex.InnerException.Message}";
                        }
                    }

                    logLine += "\n";

                    _writer.Write(logLine);
                    _writer.Flush(); // Flush explícito después de cada write

                    // También escribir a Debug para Visual Studio
                    System.Diagnostics.Debug.WriteLine(logLine);
                }
                catch (Exception writeEx)
                {
                    // Si falla, intentar fallback path
                    _lastError = $"Write failed: {writeEx.Message}";
                    try
                    {
                        var fallbackPath = Path.Combine(Path.GetTempPath(), "KCMundial_crashlog_FALLBACK.txt");
                        File.AppendAllText(fallbackPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [FALLBACK] {message}\n", Encoding.UTF8);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Obtiene la ruta del log actual
        /// </summary>
        public static string GetLogPath()
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_logPath))
                {
                    Initialize();
                }
                return _logPath;
            }
        }

        /// <summary>
        /// Obtiene el último error del logger (si hubo)
        /// </summary>
        public static string? GetLastError()
        {
            lock (_lock)
            {
                return _lastError;
            }
        }

        /// <summary>
        /// Verifica si el archivo de log existe y es escribible
        /// </summary>
        public static bool VerifyLogFile()
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_logPath))
                {
                    return false;
                }

                try
                {
                    return File.Exists(_logPath) && new FileInfo(_logPath).Length > 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Cierra el logger (llamar al cerrar la app)
        /// </summary>
        public static void Shutdown()
        {
            lock (_lock)
            {
                try
                {
                    if (_writer != null)
                    {
                        _writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SHUTDOWN] Logger closing\n");
                        _writer.Flush();
                        _writer.Close();
                    }
                }
                catch { }
                finally
                {
                    _writer?.Dispose();
                    _fileStream?.Dispose();
                    _writer = null;
                    _fileStream = null;
                    _initialized = false;
                }
            }
        }
    }
}

