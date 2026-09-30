namespace KCMundial.App.Services;

public interface INavigationService
{
    void NavigateToMain();
    void NavigateToResult(string figuritaId);
    void NavigateToGallery();
    void NavigateToGalleryDetail(string figuritaId);
    void Close();
}
