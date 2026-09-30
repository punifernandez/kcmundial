namespace KCMundial.Core.Interfaces;

/// <summary>
/// Generates unique file IDs (timestamp + short random) for raw and figurita files.
/// </summary>
public interface IFileNaming
{
    /// <summary>
    /// Generate a new unique id, e.g. "2026-02-19_19-32-10_ab12".
    /// </summary>
    string NewId();
}
