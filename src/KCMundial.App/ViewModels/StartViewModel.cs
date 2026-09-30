using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;

namespace KCMundial.App.ViewModels;

/// <summary>Primera pantalla: el invitado elige figurita (5×7, DNP) o foto grande (A4 apaisada, Epson).</summary>
public partial class StartViewModel
{
    private readonly INavigationService _navigation;
    private readonly MainViewModel _main;

    public StartViewModel(INavigationService navigation, MainViewModel main, IPathResolver paths)
    {
        _navigation = navigation;
        _main = main;
        FiguritaSample = QrImageFactory.LoadImage(paths.GetFramePath(PhotoFormat.Figurita, 1), 400);
        GrandeSample = QrImageFactory.LoadImage(paths.GetFramePath(PhotoFormat.Grande, 1), 700);
        HasGrande = paths.GetFrameCount(PhotoFormat.Grande) > 0;
    }

    public ImageSource? FiguritaSample { get; }
    public ImageSource? GrandeSample { get; }
    /// <summary>La foto grande solo se ofrece si hay un marco para ella (Grande_1.png).</summary>
    public bool HasGrande { get; }

    [RelayCommand]
    private void ChooseFigurita() => _navigation.NavigateToCapture(PhotoFormat.Figurita);

    [RelayCommand]
    private void ChooseGrande() => _navigation.NavigateToCapture(PhotoFormat.Grande);

    [RelayCommand]
    private void OpenGallery() => _navigation.NavigateToGallery();

    [RelayCommand]
    private void ToggleAdminPanel() => _main.ToggleAdminPanelCommand.Execute(null);
}
