namespace KCMundial.Core.Interfaces;

/// <summary>
/// Simple file/event logger for app lifecycle, camera, capture, processing, server.
/// </summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
