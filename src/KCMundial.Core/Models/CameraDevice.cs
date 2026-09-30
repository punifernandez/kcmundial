using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KCMundial.Core.Models;

/// <summary>
/// Represents a detected camera device for selection in the UI.
/// Single source of truth: identity is DeviceId (DeviceInformation.Id). UI shows DisplayName; system opens by DeviceId.
/// </summary>
public sealed class CameraDevice
{
    /// <summary>
    /// Human-readable name (e.g., "Logitech BRIO", "OV02C10"). Shown in combo and "Activa" label.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Full device identifier (Windows.Devices.Enumeration.DeviceInformation.Id). Used to open the camera; never use index.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Same as Id. Explicit name for "open by DeviceId" rule.
    /// </summary>
    public string DeviceId => Id;

    /// <summary>
    /// Display in UI. Never show "USB1/USB2" or index.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Cámara desconocida" : Name.Trim();

    /// <summary>
    /// Short stable key for logs (VID_PID or hash of DeviceId).
    /// </summary>
    public string StableKey => GetStableKey(Id);

    public override string ToString() => DisplayName;

    private static string GetStableKey(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return "?";
        var match = Regex.Match(deviceId, @"VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}");
        if (match.Success) return match.Value;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        return Convert.ToHexString(hash.AsSpan(0, 4));
    }
}
