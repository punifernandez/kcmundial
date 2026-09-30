using System.IO;
using System.Windows;
using System.Windows.Media;

namespace KCMundial.App;

public partial class SecondaryWindow : Window
{
    public SecondaryWindow()
    {
        InitializeComponent();
        Loaded += SecondaryWindow_Loaded;
    }

    private void SecondaryWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Posicionar en el segundo monitor
        var screens = Services.ScreenHelper.GetAllMonitors();
        if (screens.Count >= 2)
        {
            var second = screens[1];
            Left = second.Left;
            Top = second.Top;
            Width = second.Width;
            Height = second.Height;
            WindowState = WindowState.Maximized;
        }

        // Video de espera (opcional): assets/promo.mp4 o assets/idle.mp4
        var baseDir = AppContext.BaseDirectory;
        foreach (var name in new[] { "promo.mp4", "idle.mp4", "video.mp4" })
        {
            var path = Path.Combine(baseDir, "assets", name);
            if (File.Exists(path))
            {
                try
                {
                    IdleVideo.Source = new Uri(path, UriKind.Absolute);
                    IdleVideo.MediaEnded += (_, _) => IdleVideo.Position = TimeSpan.Zero;
                }
                catch { /* ignore */ }
                break;
            }
        }
    }
}
