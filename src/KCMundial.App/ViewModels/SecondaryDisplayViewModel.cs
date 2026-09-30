using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;

namespace KCMundial.App.ViewModels;

public enum SecondaryDisplayMode { Idle, Result, GalleryPhoto }

public partial class SecondaryDisplayViewModel : ObservableObject, ISecondaryDisplay
{
    private readonly IPathResolver _pathResolver;
    private string? _currentId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private SecondaryDisplayMode _mode = SecondaryDisplayMode.Idle;

    [ObservableProperty]
    private BitmapSource? _figuritaImage;

    [ObservableProperty]
    private BitmapSource? _qrImage;

    public bool IsIdle => Mode == SecondaryDisplayMode.Idle;

    public SecondaryDisplayViewModel(IPathResolver pathResolver)
    {
        _pathResolver = pathResolver;
    }

    public void ShowIdle()
    {
        _currentId = null;
        Mode = SecondaryDisplayMode.Idle;
        FiguritaImage = null;
        QrImage = null;
    }

    public void ShowResult(string figuritaId, string? qrUrl)
    {
        _currentId = figuritaId;
        FiguritaImage = QrImageFactory.LoadImage(Path.Combine(_pathResolver.FiguritasFolder, figuritaId + ".jpg"));
        QrImage = qrUrl != null ? QrImageFactory.Create(qrUrl) : null;
        Mode = SecondaryDisplayMode.Result;
    }

    public void ShowGalleryPhoto(string figuritaId)
    {
        _currentId = figuritaId;
        FiguritaImage = QrImageFactory.LoadImage(Path.Combine(_pathResolver.FiguritasFolder, figuritaId + ".jpg"));
        QrImage = null;
        Mode = SecondaryDisplayMode.GalleryPhoto;
    }

    public void SetQrUrl(string figuritaId, string qrUrl)
    {
        if (Mode == SecondaryDisplayMode.Result && figuritaId == _currentId)
            QrImage = QrImageFactory.Create(qrUrl);
    }
}
