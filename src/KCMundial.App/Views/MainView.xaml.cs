using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KCMundial.App.ViewModels;
using KCMundial.Core.Models;

namespace KCMundial.App.Views;

public partial class MainView : UserControl
{
    private bool _ignoreSelectionChange = true;
    private static readonly string IconsFolder = Path.Combine(AppContext.BaseDirectory, "assets", "icons");

    public MainView()
    {
        InitializeComponent();
        Loaded += MainView_Loaded;
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

    private void ApplyIcons()
    {
        const int iconSizeHeader = 44;  // cámara, impresora, galería, cerrar
        const int iconSizeCapture = 220; // ícono de capturar (achicado)
        const int circleStrokeThickness = 4;
        const int circleSize = iconSizeCapture + 32; // círculo que contiene el ícono

        var cameraIcon = LoadIcon("icon_camera");
        if (cameraIcon != null)
        {
            IconCamera.Source = cameraIcon;
            IconCamera.Visibility = Visibility.Visible;
        }

        var clickIcon = LoadIcon("icon_click");
        if (clickIcon != null && BtnCapture != null)
        {
            var img = new Image { Source = clickIcon, Width = iconSizeCapture, Height = iconSizeCapture };
            var strokeBrush = BtnCapture.TryFindResource("BorderBrush") as System.Windows.Media.Brush
                ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x27, 0x27, 0x2a));
            var ellipse = new System.Windows.Shapes.Ellipse
            {
                Width = circleSize,
                Height = circleSize,
                Stroke = strokeBrush,
                StrokeThickness = circleStrokeThickness,
                Fill = System.Windows.Media.Brushes.Transparent
            };
            var grid = new Grid();
            grid.Children.Add(ellipse);
            grid.Children.Add(img);
            BtnCapture.Content = grid;
        }
        else if (BtnCapture != null)
            BtnCapture.Content = "Capturar";

        var galleryIcon = LoadIcon("icon_gallery");
        if (galleryIcon != null && BtnGallery != null)
            BtnGallery.Content = new Image { Source = galleryIcon, Width = iconSizeHeader, Height = iconSizeHeader };

        var closeIcon = LoadIcon("icon_close");
        if (closeIcon != null && BtnClose != null)
            BtnClose.Content = new Image { Source = closeIcon, Width = iconSizeHeader, Height = iconSizeHeader };
    }

    private void MainView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        ApplyIcons();
        if (DataContext is MainViewModel vm)
        {
            vm.InitializationComplete += () =>
            {
                _ignoreSelectionChange = false;
            };
            
            // When returning from Result/Gallery, MainView can be recreated; InitializationComplete
            // already fired at startup, so we must allow combo changes if we're past init.
            if (vm.Cameras.Count > 0)
            {
                _ignoreSelectionChange = false;
            }
            
            // Update ComboBox when SelectedCamera changes programmatically
            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.SelectedCamera) && !_ignoreSelectionChange)
                {
                    if (vm.SelectedCamera != null && CameraComboBox.SelectedItem != vm.SelectedCamera)
                    {
                        _ignoreSelectionChange = true;
                        CameraComboBox.SelectedItem = vm.SelectedCamera;
                        _ignoreSelectionChange = false;
                    }
                }
            };
        }
    }

    private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreSelectionChange) return;
        if (e.AddedItems.Count == 0) return;
        if (DataContext is MainViewModel vm && e.AddedItems[0] is CameraDevice device)
        {
            vm.UserSelectedCamera(device);
        }
    }
}
