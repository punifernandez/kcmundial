using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;
using KCMundial.Storage;
using QRCoder;

namespace KCMundial.App.ViewModels;

public partial class GalleryDetailViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IPathResolver _pathResolver;
    private readonly LocalServerHost _serverHost;
    private readonly MetadataWriter _metadataWriter;
    private readonly string? _selectedPrinter;
    private readonly string _figuritaId;

    [ObservableProperty]
    private BitmapSource? _figuritaImage;

    [ObservableProperty]
    private BitmapSource? _qrImage;

    [ObservableProperty]
    private bool _isPrintButtonVisible = true;

    [ObservableProperty]
    private string? _printStatusMessage;

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public GalleryDetailViewModel(
        string figuritaId,
        INavigationService navigation,
        IPathResolver pathResolver,
        LocalServerHost serverHost,
        MetadataWriter metadataWriter,
        string? selectedPrinter = null)
    {
        _figuritaId = figuritaId;
        _navigation = navigation;
        _pathResolver = pathResolver;
        _serverHost = serverHost;
        _metadataWriter = metadataWriter;
        _selectedPrinter = selectedPrinter;
        LoadImage();
        GenerateQr();
    }

    private void LoadImage()
    {
        var path = Path.Combine(_pathResolver.FiguritasFolder, _figuritaId + ".jpg");
        if (!File.Exists(path)) return;
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
        catch { /* ignore */ }
    }

    private void GenerateQr()
    {
        var meta = _metadataWriter.Read(_figuritaId);
        var url = !string.IsNullOrEmpty(meta?.PermanentUrl) ? meta.PermanentUrl : $"{_serverHost.BaseUrl}/f/{_figuritaId}";
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

    [RelayCommand]
    private void Back()
    {
        _navigation.NavigateToGallery();
    }

    [RelayCommand]
    private void Delete()
    {
        GalleryViewModel.DeleteFiguritaFiles(_pathResolver, _figuritaId);
        _navigation.NavigateToGallery();
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        var path = Path.Combine(_pathResolver.FiguritasFolder, _figuritaId + ".jpg");
        if (!File.Exists(path))
        {
            MessageBox.Show("No se encontró la imagen.", "Imprimir", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IsPrintButtonVisible = false;
        var progress = new Progress<string>(msg => _dispatcher.Invoke(() => PrintStatusMessage = msg));
        try
        {
            var (success, errorMessage) = await Task.Run(() => SprocketPrintService.SendToSprocketAsync(path, progress, CancellationToken.None)).ConfigureAwait(true);
            _dispatcher.Invoke(() =>
            {
                PrintStatusMessage = null;
                if (!success)
                {
                    IsPrintButtonVisible = true;
                    MessageBox.Show(errorMessage, "Imprimir", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Invoke(() =>
            {
                PrintStatusMessage = null;
                IsPrintButtonVisible = true;
                MessageBox.Show($"Error: {ex.Message}", "Imprimir", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
    }

    private static void DrawFigurita5x7Cm(DrawingContext dc, BitmapSource bitmap, double pageWidth96, double pageHeight96)
    {
        const double figW96 = 5.0 / 2.54 * 96;
        const double figH96 = 7.0 / 2.54 * 96;
        double scale = Math.Min(figW96 / bitmap.PixelWidth, figH96 / bitmap.PixelHeight);
        double drawW = bitmap.PixelWidth * scale;
        double drawH = bitmap.PixelHeight * scale;
        double left = (pageWidth96 - drawW) / 2;
        double top = (pageHeight96 - drawH) / 2;
        dc.DrawImage(bitmap, new Rect(left, top, drawW, drawH));
    }
}
