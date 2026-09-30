using System.IO;
using System.Windows;
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
    private IAppLogger? _logger;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        var pathResolver = new PathResolver();
        pathResolver.EnsureFolders();
        var logPath = Path.Combine(pathResolver.RootInstallPath, "kcmundial.log");
        _logger = new FileAppLogger(logPath);
        _logger.Info($"KCMundial starting. Log file: {Path.GetFullPath(logPath)}");

        var fileNaming = new FileNaming();
        _cameraManager = new FallbackCameraManager(_logger);

        var cascadePath = Path.Combine(pathResolver.AssetsFolder, "haarcascade_frontalface_default.xml");
        var haar = new FaceDetector(cascadePath);
        var yunetModelPath = Path.Combine(pathResolver.AssetsFolder, "models", "face_detection_yunet_2023mar.onnx");
        IFaceDetector faceDetector;
        if (File.Exists(yunetModelPath))
        {
            var yunet = new YuNetOnnxFaceDetector(yunetModelPath, scoreThreshold: 0.4f, _logger)
            {
                UseNormalizedInput = false,
                UseRgbOrder = false
            };
            faceDetector = new FaceDetectorWithFallback(yunet, haar, _logger);
        }
        else
        {
            _logger?.Warn("YuNet model not found, using Haar cascade only");
            faceDetector = haar;
        }
        var positioningValidator = new PositioningValidator();
        var composer = new StickerComposer(pathResolver, _logger);
        var metadataWriter = new MetadataWriter(pathResolver);
        var uploadService = new PhotoUploadService(_logger);
        var exportService = new ExportService(pathResolver, fileNaming, composer, faceDetector, metadataWriter, uploadService, _logger);

        _serverHost = new LocalServerHost(pathResolver, _logger);
        await _serverHost.StartAsync();

        var mainVm = new MainViewModel(_cameraManager, faceDetector, positioningValidator, exportService, _serverHost, pathResolver, _logger);
        ISecondaryDisplay? secondaryDisplay = null;
        var screens = ScreenHelper.GetAllMonitors();
        if (screens.Count >= 2)
        {
            var secondaryVm = new SecondaryDisplayViewModel(pathResolver, _serverHost, metadataWriter);
            secondaryDisplay = secondaryVm;
            var secondaryWindow = new SecondaryWindow { DataContext = secondaryVm };
            secondaryWindow.Show();
        }
        var shell = new ShellViewModel(mainVm, pathResolver, _serverHost, metadataWriter, secondaryDisplay);

        var mainWindow = new MainWindow { DataContext = shell };
        if (screens.Count >= 1)
        {
            var primary = screens[0];
            mainWindow.Left = primary.Left;
            mainWindow.Top = primary.Top;
            mainWindow.Width = primary.Width;
            mainWindow.Height = primary.Height;
        }
        mainWindow.Show();

        await mainVm.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.Info("Application shutting down...");
        
        // Stop camera first (synchronous, should be fast)
        try
        {
            _cameraManager?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.Error("Error disposing camera manager", ex);
        }

        // Stop server with timeout (don't block too long)
        try
        {
            var stopTask = _serverHost?.StopAsync();
            if (stopTask != null)
            {
                var completed = Task.WaitAny(stopTask, Task.Delay(1000));
                if (completed != 0)
                    _logger?.Warn("Server stop timed out, forcing exit");
            }
        }
        catch (Exception ex)
        {
            _logger?.Error("Error stopping server", ex);
        }

        _logger?.Info("Application shutdown complete");
        base.OnExit(e);
    }
}
