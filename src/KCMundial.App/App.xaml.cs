using System.IO;
using System.Windows;
using System.Windows.Threading;
using KCMundial.App.Services;
using KCMundial.App.ViewModels;
using KCMundial.Camera;
using KCMundial.Core.Interfaces;
using KCMundial.Processing;
using KCMundial.Storage;
using KCMundial.Vision;

namespace KCMundial.App;

public partial class App : Application
{
    private LocalServerHost? _serverHost;
    private ICameraManager? _cameraManager;
    private FiguritaComposer? _composer;
    private IAppLogger? _logger;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var pathResolver = new PathResolver();
        pathResolver.EnsureFolders();
        _logger = new FileAppLogger(Path.Combine(pathResolver.RootInstallPath, "kcmundial.log"));
        _logger.Info($"KCMundial starting in {pathResolver.RootInstallPath}");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        var settings = AppSettings.Load(pathResolver.RootInstallPath, _logger);
        _cameraManager = new FallbackCameraManager(_logger);

        var haar = new FaceDetector(Path.Combine(pathResolver.AssetsFolder, "haarcascade_frontalface_default.xml"));
        var yunetModelPath = Path.Combine(pathResolver.AssetsFolder, "models", "face_detection_yunet_2023mar.onnx");
        IFaceDetector faceDetector = File.Exists(yunetModelPath)
            ? new FaceDetectorWithFallback(
                new YuNetOnnxFaceDetector(yunetModelPath, scoreThreshold: 0.4f, _logger) { UseNormalizedInput = false, UseRgbOrder = false },
                haar, _logger)
            : haar;

        var metadataWriter = new MetadataWriter(pathResolver);
        _composer = new FiguritaComposer(_logger);
        var printPage = new PrintPageSpec(settings.PrintPageWidthInches, settings.PrintPageHeightInches, settings.PrintMarginMm);
        var exportService = new ExportService(pathResolver, new FileNaming(), _composer, printPage, metadataWriter,
            settings.UploadEnabled ? new PhotoUploadService(_logger) : null, _logger);
        var printer = new PhotoPrinter(settings, _logger);

        _serverHost = new LocalServerHost(pathResolver, _logger);
        await _serverHost.StartAsync();

        var mainVm = new MainViewModel(_cameraManager, faceDetector, new PositioningValidator(), exportService, pathResolver, settings, _logger);

        var monitors = ScreenHelper.GetAllMonitors();
        _logger.Info("Monitors: " + string.Join("; ", monitors.Select(m => $"{m.DeviceName} {m.Width}x{m.Height}@{m.Left},{m.Top}{(m.IsPrimary ? " primary" : "")}")));

        ISecondaryDisplay? secondaryDisplay = null;
        if (monitors.Count >= 2)
        {
            var secondaryVm = new SecondaryDisplayViewModel(pathResolver);
            secondaryDisplay = secondaryVm;
            ScreenHelper.ShowFullScreenOn(new SecondaryWindow(pathResolver.AssetsFolder, _logger) { DataContext = secondaryVm }, monitors[1]);
        }

        var shell = new ShellViewModel(mainVm, exportService, pathResolver, _serverHost, metadataWriter, printer, settings, secondaryDisplay);
        var mainWindow = new MainWindow { DataContext = shell };
        MainWindow = mainWindow;
        if (monitors.Count >= 1)
            ScreenHelper.ShowFullScreenOn(mainWindow, monitors[0]);
        else
            mainWindow.Show();

        await mainVm.InitializeAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // En un evento no queremos que la app se cierre sola: se registra y se sigue.
        _logger?.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info("Application shutting down...");
        try { _cameraManager?.Dispose(); }
        catch (Exception ex) { _logger?.Error("Error disposing camera manager", ex); }

        try
        {
            var stopTask = _serverHost?.StopAsync();
            if (stopTask != null && Task.WaitAny(stopTask, Task.Delay(1000)) != 0)
                _logger?.Warn("Server stop timed out, forcing exit");
        }
        catch (Exception ex) { _logger?.Error("Error stopping server", ex); }

        _composer?.Dispose();
        _logger?.Info("Application shutdown complete");
        base.OnExit(e);
    }
}
