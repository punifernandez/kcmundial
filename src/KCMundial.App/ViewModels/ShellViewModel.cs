using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;
using KCMundial.Storage;

namespace KCMundial.App.ViewModels;

public partial class ShellViewModel : ObservableObject, INavigationService
{
    [ObservableProperty]
    private object? _currentViewModel;

    private readonly MainViewModel _mainViewModel;
    private readonly IPathResolver _pathResolver;
    private readonly LocalServerHost _serverHost;
    private readonly MetadataWriter _metadataWriter;
    private readonly ISecondaryDisplay? _secondaryDisplay;

    public ShellViewModel(
        MainViewModel mainViewModel,
        IPathResolver pathResolver,
        LocalServerHost serverHost,
        MetadataWriter metadataWriter,
        ISecondaryDisplay? secondaryDisplay = null)
    {
        _mainViewModel = mainViewModel;
        _pathResolver = pathResolver;
        _serverHost = serverHost;
        _metadataWriter = metadataWriter;
        _secondaryDisplay = secondaryDisplay;
        _mainViewModel.SetNavigation(this);
        CurrentViewModel = _mainViewModel;
    }

    public void NavigateToMain()
    {
        CurrentViewModel = _mainViewModel;
        _mainViewModel.OnReturnFromResult();
        _secondaryDisplay?.ShowIdle();
    }

    public void NavigateToResult(string figuritaId)
    {
        CurrentViewModel = new ResultViewModel(figuritaId, this, _pathResolver, _serverHost, _metadataWriter, null);
        _secondaryDisplay?.ShowResult(figuritaId);
    }

    public void NavigateToGallery()
    {
        CurrentViewModel = new GalleryViewModel(this, _pathResolver);
        _secondaryDisplay?.ShowIdle();
    }

    public void NavigateToGalleryDetail(string figuritaId)
    {
        CurrentViewModel = new GalleryDetailViewModel(figuritaId, this, _pathResolver, _serverHost, _metadataWriter, null);
        _secondaryDisplay?.ShowGalleryPhoto(figuritaId);
    }

    private bool _isClosing;

    [ObservableProperty]
    private bool _showCloseConfirmOverlay;

    public void Close()
    {
        if (_isClosing) return;
        ShowCloseConfirmOverlay = true;
    }

    [RelayCommand]
    private void CloseApp()
    {
        Close();
    }

    [RelayCommand]
    private void ConfirmCloseYes()
    {
        ShowCloseConfirmOverlay = false;
        _isClosing = true;
        Application.Current.Shutdown();
    }

    [RelayCommand]
    private void ConfirmCloseNo()
    {
        ShowCloseConfirmOverlay = false;
    }
}
