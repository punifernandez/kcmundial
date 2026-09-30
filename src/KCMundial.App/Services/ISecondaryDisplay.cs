namespace KCMundial.App.Services;

/// <summary>Contenido del monitor secundario (idle = video, resultado + QR, foto de galería).</summary>
public interface ISecondaryDisplay
{
    void ShowIdle();
    void ShowResult(string figuritaId, string? qrUrl);
    void ShowGalleryPhoto(string figuritaId);
    /// <summary>Llegó el link del QR de una foto (puede ser la que se está mostrando).</summary>
    void SetQrUrl(string figuritaId, string qrUrl);
}
