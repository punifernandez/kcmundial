using System.IO;
using System.Windows;
using KCMundial.App.ViewModels;
using KCMundial.Core.Interfaces;

namespace KCMundial.App;

public partial class SecondaryWindow : Window
{
    private static readonly string[] VideoNames = { "promo", "idle", "video" };
    private static readonly string[] VideoExtensions = { ".mp4", ".wmv", ".mov", ".avi" };

    public SecondaryWindow(string assetsFolder, IAppLogger? logger)
    {
        InitializeComponent();

        var video = VideoNames.SelectMany(n => VideoExtensions.Select(ext => Path.Combine(assetsFolder, n + ext))).FirstOrDefault(File.Exists);
        if (video != null)
        {
            logger?.Info($"SecondaryWindow: idle video {video}");
            IdleVideo.Source = new Uri(video, UriKind.Absolute);
            IdleVideo.MediaEnded += (_, _) =>
            {
                IdleVideo.Position = TimeSpan.Zero;
                IdleVideo.Play();
            };
            IdleVideo.MediaFailed += (_, args) => logger?.Error("SecondaryWindow: video failed", args.ErrorException);
        }
        else
        {
            logger?.Info("SecondaryWindow: no idle video found (assets/promo.mp4)");
        }

        Loaded += (_, _) => UpdateVideo();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is SecondaryDisplayViewModel oldVm) oldVm.PropertyChanged -= OnVmPropertyChanged;
            if (args.NewValue is SecondaryDisplayViewModel newVm) newVm.PropertyChanged += OnVmPropertyChanged;
        };
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SecondaryDisplayViewModel.Mode))
            UpdateVideo();
    }

    /// <summary>El video solo corre cuando se ve (no gasta CPU/GPU mientras se muestra una foto).</summary>
    private void UpdateVideo()
    {
        if (IdleVideo.Source == null) return;
        if (DataContext is SecondaryDisplayViewModel { IsIdle: true })
            IdleVideo.Play();
        else
            IdleVideo.Pause();
    }
}
