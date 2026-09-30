using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;
using KCMundial.Processing;
using KCMundial.Storage;

namespace KCMundial.App.ViewModels;

public partial class ShellViewModel : ObservableObject, INavigationService
{
    [ObservableProperty]
    private object? _currentViewModel;

    [ObservableProperty]
    private bool _showCloseConfirmOverlay;

    private readonly MainViewModel _mainViewModel;
    private readonly IPathResolver _pathResolver;
    private readonly LocalServerHost _serverHost;
    private readonly MetadataWriter _metadataWriter;
    private readonly PhotoPrinter _printer;
    private readonly AppSettings _settings;
    private readonly ISecondaryDisplay? _secondaryDisplay;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    /// <summary>Resultado de subidas que terminaron (id → url, null si falló).</summary>
    private readonly ConcurrentDictionary<string, string?> _uploads = new();
    private bool _isClosing;

    public ShellViewModel(
        MainViewModel mainViewModel,
        ExportService exportService,
        IPathResolver pathResolver,
        LocalServerHost serverHost,
        MetadataWriter metadataWriter,
        PhotoPrinter printer,
        AppSettings settings,
        ISecondaryDisplay? secondaryDisplay = null)
    {
        _mainViewModel = mainViewModel;
        _pathResolver = pathResolver;
        _serverHost = serverHost;
        _metadataWriter = metadataWriter;
        _printer = printer;
        _settings = settings;
        _secondaryDisplay = secondaryDisplay;
        _mainViewModel.SetNavigation(this);
        exportService.UploadFinished += OnUploadFinished;
        CurrentViewModel = _mainViewModel;
    }

    private string LocalUrl(string id) => $"{_serverHost.BaseUrl}/f/{id}";

    /// <summary>Link del QR: el permanente si ya se subió; si la subida falló, el del servidor local; si está subiendo, null.</summary>
    private string? QrUrlFor(string id)
    {
        var permanent = _metadataWriter.Read(id)?.PermanentUrl;
        if (!string.IsNullOrEmpty(permanent)) return permanent;
        if (!_settings.UploadEnabled) return LocalUrl(id);
        return _uploads.TryGetValue(id, out var url) ? url ?? LocalUrl(id) : null;
    }

    private void OnUploadFinished(string id, string? url)
    {
        _uploads[id] = url;
        _dispatcher.BeginInvoke(() =>
        {
            var qrUrl = url ?? LocalUrl(id);
            if (CurrentViewModel is ResultViewModel result && result.FiguritaId == id && result.QrImage == null)
                result.SetQrUrl(qrUrl);
            _secondaryDisplay?.SetQrUrl(id, qrUrl);
        });
    }

    public void NavigateToMain()
    {
        CurrentViewModel = _mainViewModel;
        _mainViewModel.OnReturnFromResult();
        _secondaryDisplay?.ShowIdle();
    }

    public void NavigateToResult(ExportResult result)
    {
        var qrUrl = QrUrlFor(result.Id);
        CurrentViewModel = new ResultViewModel(result.Id, result.FiguritaPath, result.PrintPath, qrUrl, this, _printer, _settings);
        _secondaryDisplay?.ShowResult(result.Id, qrUrl);
    }

    public void NavigateToGallery()
    {
        CurrentViewModel = new GalleryViewModel(this, _pathResolver);
        _secondaryDisplay?.ShowIdle();
    }

    public void NavigateToGalleryDetail(string figuritaId)
    {
        var displayPath = Path.Combine(_pathResolver.FiguritasFolder, figuritaId + ".jpg");
        // Fotos viejas pueden no tener hoja de impresión: se imprime la figurita (el printer la ajusta sin recortar).
        var printPath = Path.Combine(_pathResolver.ImpresionFolder, figuritaId + ".jpg");
        if (!File.Exists(printPath)) printPath = displayPath;
        CurrentViewModel = new GalleryDetailViewModel(figuritaId, displayPath, printPath, QrUrlFor(figuritaId) ?? LocalUrl(figuritaId), this, _pathResolver, _printer, _settings);
        _secondaryDisplay?.ShowGalleryPhoto(figuritaId);
    }

    public void Close()
    {
        if (_isClosing) return;
        ShowCloseConfirmOverlay = true;
    }

    [RelayCommand]
    private void CloseApp() => Close();

    [RelayCommand]
    private void ConfirmCloseYes()
    {
        ShowCloseConfirmOverlay = false;
        _isClosing = true;
        Application.Current.Shutdown();
    }

    [RelayCommand]
    private void ConfirmCloseNo() => ShowCloseConfirmOverlay = false;
}
