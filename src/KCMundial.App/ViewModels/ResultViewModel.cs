using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;

namespace KCMundial.App.ViewModels;

public enum PrintState { Idle, Printing, Done, Error }

/// <summary>Estado de una impresora (chica o XL) para una foto: qué archivo se imprime y cómo va.</summary>
public partial class PrintJobViewModel : ObservableObject
{
    private readonly PhotoPrinter _printer;
    private readonly string _path;

    public PrintJobViewModel(string label, PhotoPrinter printer, string path)
    {
        Label = label;
        _printer = printer;
        _path = path;
    }

    /// <summary>Nombre para mostrar ("Figurita", "Foto XL").</summary>
    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrinting))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private PrintState _state = PrintState.Idle;

    [ObservableProperty]
    private string? _statusMessage;

    public bool IsPrinting => State == PrintState.Printing;
    public bool IsError => State == PrintState.Error;
    public bool IsDone => State == PrintState.Done;

    private bool CanPrint() => State != PrintState.Printing;

    [RelayCommand(CanExecute = nameof(CanPrint))]
    public async Task PrintAsync()
    {
        State = PrintState.Printing;
        StatusMessage = "Imprimiendo…";
        try
        {
            var outcome = await _printer.PrintAsync(_path);
            State = outcome.Success ? PrintState.Done : PrintState.Error;
            StatusMessage = outcome.Message;
        }
        catch (Exception ex)
        {
            State = PrintState.Error;
            StatusMessage = $"No se pudo imprimir: {ex.Message}";
        }
    }
}

/// <summary>Lo común a la pantalla de resultado y al detalle de galería: imagen, QR e impresión chica y XL.</summary>
public abstract partial class PrintableFiguritaViewModel : ObservableObject
{
    protected PrintableFiguritaViewModel(string figuritaId, FiguritaFiles files, PhotoPrinter smallPrinter, PhotoPrinter xlPrinter)
    {
        FiguritaId = figuritaId;
        FiguritaImage = QrImageFactory.LoadImage(files.DisplayPath);
        Small = new PrintJobViewModel("Figurita", smallPrinter, files.SmallPrintPath);
        Xl = new PrintJobViewModel("Foto XL", xlPrinter, files.XlPrintPath);
    }

    public string FiguritaId { get; }

    /// <summary>Figurita chica en la DNP.</summary>
    public PrintJobViewModel Small { get; }

    /// <summary>Ampliación en la impresora XL (A4): el máster 5:7 entero, casi a hoja completa.</summary>
    public PrintJobViewModel Xl { get; }

    [ObservableProperty]
    private BitmapSource? _figuritaImage;

    [ObservableProperty]
    private BitmapSource? _qrImage;

    public void SetQrUrl(string url) => QrImage = QrImageFactory.Create(url);
}

/// <summary>Rutas de los archivos de una foto: pantalla, hoja de la DNP y máster para la XL (con respaldo para fotos viejas).</summary>
public sealed record FiguritaFiles(string DisplayPath, string SmallPrintPath, string XlPrintPath)
{
    public static FiguritaFiles For(Core.Interfaces.IPathResolver paths, string id)
    {
        var display = Path.Combine(paths.FiguritasFolder, id + ".jpg");
        string FirstExisting(params string[] candidates) => candidates.FirstOrDefault(File.Exists) ?? display;
        return new FiguritaFiles(
            display,
            FirstExisting(Path.Combine(paths.ImpresionFolder, id + ".jpg"), display),
            FirstExisting(Path.Combine(paths.FiguritasHdFolder, id + ".jpg"), display));
    }
}

/// <summary>
/// Pantalla después de la foto: la figurita chica se imprime sola; el invitado puede imprimir otra, pedir la XL
/// o sacar otra foto. No vuelve sola al inicio.
/// </summary>
public partial class ResultViewModel : PrintableFiguritaViewModel
{
    private readonly INavigationService _navigation;

    public ResultViewModel(string figuritaId, FiguritaFiles files, string? qrUrl, INavigationService navigation,
        PhotoPrinter smallPrinter, PhotoPrinter xlPrinter, AppSettings settings)
        : base(figuritaId, files, smallPrinter, xlPrinter)
    {
        _navigation = navigation;
        if (qrUrl != null) SetQrUrl(qrUrl);
        if (settings.AutoPrint)
            _ = Small.PrintAsync();
    }

    [RelayCommand]
    private void Back() => _navigation.NavigateToMain();
}
