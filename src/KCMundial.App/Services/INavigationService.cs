using KCMundial.Processing;

namespace KCMundial.App.Services;

public interface INavigationService
{
    void NavigateToMain();
    void NavigateToResult(ExportResult result);
    void NavigateToGallery();
    void NavigateToGalleryDetail(string figuritaId);
    void Close();
}
