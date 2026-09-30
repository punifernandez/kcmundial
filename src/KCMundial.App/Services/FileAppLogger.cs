using System.IO;
using KCMundial.Core.Interfaces;

namespace KCMundial.App.Services;

public sealed class FileAppLogger : IAppLogger
{
    private readonly string _logPath;
    private readonly object _lock = new();

    public FileAppLogger(string logPath)
    {
        _logPath = logPath;
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private void Write(string level, string message, Exception? ex)
    {
        var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        if (ex != null)
            line += Environment.NewLine + ex.ToString();
        lock (_lock)
        {
            try { File.AppendAllText(_logPath, line + Environment.NewLine); } catch { /* ignore */ }
        }
    }
}
