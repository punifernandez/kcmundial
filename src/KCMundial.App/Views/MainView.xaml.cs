using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using KCMundial.App.ViewModels;
using KCMundial.Core.Models;

namespace KCMundial.App.Views;

public partial class MainView : UserControl
{
    private MainViewModel? _vm;

    public MainView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _vm = DataContext as MainViewModel;
            if (_vm == null) return;
            _vm.FlashRequested -= PlayFlash;
            _vm.FlashRequested += PlayFlash;
        };
        Unloaded += (_, _) =>
        {
            if (_vm != null) _vm.FlashRequested -= PlayFlash;
        };
    }

    private void PlayFlash()
    {
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        FlashRect.BeginAnimation(OpacityProperty, animation);
    }

    private void CameraComboBox_DropDownClosed(object? sender, EventArgs e)
    {
        // Solo cuando el operador elige en la lista (no cuando el binding actualiza la selección).
        if (CameraComboBox.SelectedItem is CameraDevice device)
            _vm?.UserSelectedCamera(device);
    }
}
