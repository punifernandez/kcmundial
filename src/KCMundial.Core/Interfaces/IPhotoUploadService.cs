namespace KCMundial.Core.Interfaces;

/// <summary>Sube la foto al servidor de hosting y devuelve la URL pública para el QR.</summary>
public interface IPhotoUploadService
{
    /// <summary>Sube el JPEG y devuelve la URL permanente (para generar el QR). Null si falla.</summary>
    Task<string?> UploadAsync(byte[] jpegBytes, string suggestedFileName, CancellationToken cancellationToken = default);
}
