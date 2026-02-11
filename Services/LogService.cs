using System;
using System.IO;

namespace KCMundial.Services
{
    /// <summary>
    /// Servicio para escribir logs a archivo
    /// </summary>
    public static class LogService
    {
        private static string? _logFilePath;

        static LogService()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var logDir = Path.Combine(desktop, "KCMundial", "Logs");
                Directory.CreateDirectory(logDir);
                
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _logFilePath = Path.Combine(logDir, $"log_{timestamp}.txt");
                
                Write("=== LOG INICIADO ===");
                Write($"Fecha: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch
            {
                // Si falla, usar ubicación temporal
                _logFilePath = Path.Combine(Path.GetTempPath(), $"kcmundial_log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            }
        }

        public static void Write(string message)
        {
            try
            {
                var logMessage = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
                
                // Escribir a Debug (para Visual Studio)
                System.Diagnostics.Debug.WriteLine(logMessage);
                
                // Escribir a archivo
                if (!string.IsNullOrEmpty(_logFilePath))
                {
                    File.AppendAllText(_logFilePath, logMessage + Environment.NewLine);
                }
            }
            catch
            {
                // Ignorar errores de logging
            }
        }

        public static string GetLogFilePath() => _logFilePath ?? "";
    }
}

