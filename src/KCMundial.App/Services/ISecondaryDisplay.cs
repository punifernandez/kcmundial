namespace KCMundial.App.Services;

/// <summary>Contenido a mostrar en el monitor secundario (idle = video/anim, resultado+QR, foto de galería).</summary>
public interface ISecondaryDisplay
{
    void ShowIdle();
    void ShowResult(string figuritaId);
    void ShowGalleryPhoto(string figuritaId);
}
