using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;

namespace KCMundial.App.ViewModels;

public partial class GalleryViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IPathResolver _pathResolver;

    [ObservableProperty]
    private ObservableCollection<FiguritaItem> _items = new();

    public GalleryViewModel(INavigationService navigation, IPathResolver pathResolver)
    {
        _navigation = navigation;
        _pathResolver = pathResolver;
        LoadItems();
    }

    private void LoadItems()
    {
        Items.Clear();
        var folder = _pathResolver.FiguritasFolder;
        if (!Directory.Exists(folder)) return;

        var idsWithDate = new List<(string id, DateTime capturedAt)>();
        foreach (var fi in Directory.GetFiles(folder, "*.jpg"))
        {
            var id = Path.GetFileNameWithoutExtension(fi);
            if (string.IsNullOrEmpty(id)) continue;
            var capturedAt = TryGetCapturedAt(id, fi);
            idsWithDate.Add((id, capturedAt));
        }

        foreach (var (id, _) in idsWithDate.OrderByDescending(x => x.capturedAt))
        {
            var path = Path.Combine(folder, id + ".jpg");
            BitmapSource? thumb = null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(path, UriKind.Absolute);
                img.DecodePixelWidth = 200;
                img.EndInit();
                img.Freeze();
                thumb = img;
            }
            catch { /* ignore */ }
            var item = new FiguritaItem { Id = id, Thumbnail = thumb };
            item.SelectionChanged = NotifySelectionChanged;
            Items.Add(item);
        }
    }

    private DateTime TryGetCapturedAt(string id, string jpgPath)
    {
        var jsonPath = Path.Combine(_pathResolver.FiguritasFolder, id + ".json");
        try
        {
            if (File.Exists(jsonPath))
            {
                var json = File.ReadAllText(jsonPath);
                var meta = JsonSerializer.Deserialize<FiguritaMetadata>(json);
                if (meta != null && meta.CreatedAt != default)
                    return meta.CreatedAt;
            }
        }
        catch { /* ignore */ }
        try
        {
            return File.GetLastWriteTimeUtc(jpgPath);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Delete all files for a figurita id (figuritas, figuritas_hd, raw, raw_debug, metadata).</summary>
    public static void DeleteFiguritaFiles(IPathResolver pathResolver, string id)
    {
        TryDelete(Path.Combine(pathResolver.FiguritasFolder, id + ".jpg"));
        TryDelete(Path.Combine(pathResolver.FiguritasFolder, id + ".json"));
        TryDelete(Path.Combine(pathResolver.FiguritasHdFolder, id + ".jpg"));
        TryDelete(Path.Combine(pathResolver.RawFolder, id + ".jpg"));
        TryDelete(Path.Combine(pathResolver.RawDebugFolder, id + ".jpg"));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private bool HasSelection => Items.Any(i => i.IsSelected);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelection()
    {
        var toRemove = Items.Where(i => i.IsSelected).Select(i => i.Id).ToList();
        foreach (var id in toRemove)
            DeleteFiguritaFiles(_pathResolver, id);
        LoadItems();
    }

    internal void NotifySelectionChanged() => DeleteSelectionCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var item in Items)
            item.IsSelected = false;
    }

    [RelayCommand]
    private void Back()
    {
        _navigation.NavigateToMain();
    }

    [RelayCommand]
    private void OpenDetail(FiguritaItem? item)
    {
        if (item != null)
            _navigation.NavigateToGalleryDetail(item.Id);
    }
}

public partial class FiguritaItem : ObservableObject
{
    public string Id { get; set; } = string.Empty;
    public BitmapSource? Thumbnail { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    internal Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}
