using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;

namespace KCMundial.App.ViewModels;

public partial class GalleryDetailViewModel : PrintableFiguritaViewModel
{
    private readonly INavigationService _navigation;
    private readonly Core.Interfaces.IPathResolver _pathResolver;

    public GalleryDetailViewModel(string figuritaId, string printPath, string qrUrl, INavigationService navigation,
        Core.Interfaces.IPathResolver pathResolver, PhotoPrinter printer, AppSettings settings)
        : base(figuritaId, printPath, printer, settings.PrintCopies)
    {
        _navigation = navigation;
        _pathResolver = pathResolver;
        SetQrUrl(qrUrl);
    }

    [RelayCommand]
    private void Back() => _navigation.NavigateToGallery();

    [RelayCommand]
    private void Delete()
    {
        GalleryViewModel.DeleteFiguritaFiles(_pathResolver, FiguritaId);
        _navigation.NavigateToGallery();
    }
}
