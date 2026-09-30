using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KCMundial.App;

public partial class MainWindow : Window
{
    private static readonly string IconsFolder = Path.Combine(AppContext.BaseDirectory, "assets", "icons");

    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        const int iconSize = 44;
        var icon = LoadIcon("icon_close");
        if (icon != null && BtnClose != null)
            BtnClose.Content = new Image { Source = icon, Width = iconSize, Height = iconSize };
    }

    private static ImageSource? LoadIcon(string name)
    {
        foreach (var ext in new[] { ".png", ".ico" })
        {
            var path = Path.Combine(IconsFolder, name + ext);
            if (!File.Exists(path)) continue;
            try
            {
                var uri = new Uri(path, UriKind.Absolute);
                if (ext == ".ico")
                {
                    var frame = BitmapFrame.Create(uri);
                    frame.Freeze();
                    return frame;
                }
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { /* ignore */ }
        }
        return null;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Prevent automatic close, show confirmation dialog
        e.Cancel = true;
        if (DataContext is ViewModels.ShellViewModel shell)
        {
            shell.Close();
        }
    }
}
