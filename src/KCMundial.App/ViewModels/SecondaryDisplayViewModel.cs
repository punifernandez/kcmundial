using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;
using KCMundial.Storage;
using QRCoder;

namespace KCMundial.App.ViewModels;

public enum SecondaryDisplayMode { Idle, Result, GalleryPhoto }

public partial class SecondaryDisplayViewModel : ObservableObject, ISecondaryDisplay
{
    private readonly IPathResolver _pathResolver;
    private readonly LocalServerHost _serverHost;
    private readonly MetadataWriter _metadataWriter;

    [ObservableProperty]
    private SecondaryDisplayMode _mode = SecondaryDisplayMode.Idle;

    [ObservableProperty]
    private BitmapSource? _figuritaImage;

    [ObservableProperty]
    private BitmapSource? _qrImage;

    public SecondaryDisplayViewModel(IPathResolver pathResolver, LocalServerHost serverHost, MetadataWriter metadataWriter)
    {
        _pathResolver = pathResolver;
        _serverHost = serverHost;
        _metadataWriter = metadataWriter;
    }

    public void ShowIdle()
    {
        Mode = SecondaryDisplayMode.Idle;
        FiguritaImage = null;
        QrImage = null;
    }

    public void ShowResult(string figuritaId)
    {
        Mode = SecondaryDisplayMode.Result;
        LoadFiguritaImage(figuritaId);
        GenerateQr(figuritaId);
    }

    public void ShowGalleryPhoto(string figuritaId)
    {
        Mode = SecondaryDisplayMode.GalleryPhoto;
        LoadFiguritaImage(figuritaId);
        QrImage = null;
    }

    private void LoadFiguritaImage(string figuritaId)
    {
        var path = Path.Combine(_pathResolver.FiguritasFolder, figuritaId + ".jpg");
        if (!File.Exists(path))
        {
            FiguritaImage = null;
            return;
        }
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            FiguritaImage = bitmap;
        }
        catch
        {
            FiguritaImage = null;
        }
    }

    private void GenerateQr(string figuritaId)
    {
        var meta = _metadataWriter.Read(figuritaId);
        var url = !string.IsNullOrEmpty(meta?.PermanentUrl) ? meta.PermanentUrl : $"{_serverHost.BaseUrl}/f/{figuritaId}";
        using var qr = new QRCodeGenerator();
        using var data = qr.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        var png = code.GetGraphic(4);
        using var ms = new MemoryStream(png);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        QrImage = bitmap;
    }
}
