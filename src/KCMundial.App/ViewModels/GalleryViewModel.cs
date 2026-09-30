using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;

namespace KCMundial.App.ViewModels;

public partial class GalleryViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IPathResolver _pathResolver;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private ObservableCollection<FiguritaItem> _items = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItems))]
    private int _selectedCount;

    public bool HasSelectedItems => SelectedCount > 0;

    public GalleryViewModel(INavigationService navigation, IPathResolver pathResolver)
    {
        _navigation = navigation;
        _pathResolver = pathResolver;
        LoadItems();
    }

    public string Summary => Items.Count == 1 ? "1 foto" : $"{Items.Count} fotos";

    /// <summary>Lista las fotos (más nuevas primero) y carga las miniaturas en segundo plano.</summary>
    private void LoadItems()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        Items.Clear();
        SelectedCount = 0;
        var folder = _pathResolver.FiguritasFolder;
        if (Directory.Exists(folder))
        {
            // Los ids empiezan con fecha y hora, así que el nombre alcanza para ordenar.
            foreach (var path in new DirectoryInfo(folder).GetFiles("*.jpg").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                var item = new FiguritaItem { Id = Path.GetFileNameWithoutExtension(path.Name), Path = path.FullName };
                item.SelectionChanged = NotifySelectionChanged;
                Items.Add(item);
            }
        }
        OnPropertyChanged(nameof(Summary));

        var snapshot = Items.ToList();
        Task.Run(() =>
        {
            foreach (var item in snapshot)
            {
                if (token.IsCancellationRequested) return;
                var thumb = QrImageFactory.LoadImage(item.Path, 300);
                _dispatcher.BeginInvoke(() => item.Thumbnail = thumb, DispatcherPriority.Background);
            }
        }, token);
    }

    /// <summary>Borra todos los archivos de una foto.</summary>
    public static void DeleteFiguritaFiles(IPathResolver pathResolver, string id)
    {
        TryDelete(Path.Combine(pathResolver.FiguritasFolder, id + ".jpg"));
        TryDelete(Path.Combine(pathResolver.FiguritasFolder, id + ".json"));
        TryDelete(Path.Combine(pathResolver.ImpresionFolder, id + ".jpg"));
        TryDelete(Path.Combine(pathResolver.FiguritasHdFolder, id + ".jpg"));
        TryDelete(Path.Combine(pathResolver.Ampliaciones20x30Folder, id + ".jpg"));
        TryDelete(Path.Combine(pathResolver.RawFolder, id + ".jpg"));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedItems))]
    private void DeleteSelection()
    {
        foreach (var id in Items.Where(i => i.IsSelected).Select(i => i.Id).ToList())
            DeleteFiguritaFiles(_pathResolver, id);
        LoadItems();
    }

    internal void NotifySelectionChanged()
    {
        SelectedCount = Items.Count(i => i.IsSelected);
        DeleteSelectionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }

    [RelayCommand]
    private void Back()
    {
        _loadCts?.Cancel();
        _navigation.NavigateToMain();
    }

    [RelayCommand]
    private void OpenDetail(FiguritaItem? item)
    {
        if (item == null) return;
        _loadCts?.Cancel();
        _navigation.NavigateToGalleryDetail(item.Id);
    }
}

public partial class FiguritaItem : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    internal Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}
