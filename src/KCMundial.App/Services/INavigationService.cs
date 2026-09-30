using KCMundial.Core.Models;
using KCMundial.Processing;

namespace KCMundial.App.Services;

public interface INavigationService
{
    /// <summary>Pantalla de inicio: elegir figurita o foto grande.</summary>
    void NavigateToStart();
    /// <summary>Preview y captura para el formato elegido.</summary>
    void NavigateToCapture(PhotoFormat format);
    void NavigateToResult(ExportResult result);
    void NavigateToGallery();
    void NavigateToGalleryDetail(string figuritaId);
    void Close();
}
