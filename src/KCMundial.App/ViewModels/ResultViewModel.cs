using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;
using KCMundial.Storage;

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

/// <summary>Lo común a la pantalla de resultado y al detalle de galería: imagen, QR e impresión en su tamaño.</summary>
public abstract partial class PrintableFiguritaViewModel : ObservableObject
{
    protected PrintableFiguritaViewModel(string figuritaId, FiguritaFiles files, PhotoPrinter printer)
    {
        FiguritaId = figuritaId;
        Format = files.Format;
        FiguritaImage = QrImageFactory.LoadImage(files.DisplayPath);
        Print = new PrintJobViewModel(files.Format == PhotoFormat.Grande ? "Foto grande" : "Figurita", printer, files.PrintPath);
    }

    public string FiguritaId { get; }
    public PhotoFormat Format { get; }
    public bool IsGrande => Format == PhotoFormat.Grande;

    /// <summary>Impresión en el tamaño de la foto (DNP para la figurita, Epson A4 para la grande).</summary>
    public PrintJobViewModel Print { get; }

    [ObservableProperty]
    private BitmapSource? _figuritaImage;

    [ObservableProperty]
    private BitmapSource? _qrImage;

    public void SetQrUrl(string url) => QrImage = QrImageFactory.Create(url);
}

/// <summary>Rutas y formato de una foto: la versión de pantalla y lo que se imprime.</summary>
public sealed record FiguritaFiles(string DisplayPath, string PrintPath, PhotoFormat Format)
{
    /// <summary>Para la galería: el formato sale de la metadata (las fotos viejas son figuritas).</summary>
    public static FiguritaFiles For(IPathResolver paths, MetadataWriter metadata, string id)
    {
        var display = Path.Combine(paths.FiguritasFolder, id + ".jpg");
        var format = metadata.Read(id)?.Format ?? PhotoFormat.Figurita;
        string FirstExisting(params string[] candidates) => candidates.FirstOrDefault(File.Exists) ?? display;
        var print = format == PhotoFormat.Grande
            ? FirstExisting(Path.Combine(paths.FiguritasHdFolder, id + ".jpg"), display)
            : FirstExisting(Path.Combine(paths.ImpresionFolder, id + ".jpg"), display);
        return new FiguritaFiles(display, print, format);
    }
}

/// <summary>
/// Pantalla después de la foto: se imprime sola una vez en su tamaño; el invitado puede imprimir de nuevo o sacar
/// otra foto. Queda esperando (para leer el QR o pedir otra copia): no vuelve sola al inicio.
/// </summary>
public partial class ResultViewModel : PrintableFiguritaViewModel
{
    private readonly INavigationService _navigation;

    public ResultViewModel(string figuritaId, FiguritaFiles files, string? qrUrl, INavigationService navigation,
        PhotoPrinter printer, AppSettings settings)
        : base(figuritaId, files, printer)
    {
        _navigation = navigation;
        if (qrUrl != null) SetQrUrl(qrUrl);
        if (settings.AutoPrint)
            _ = Print.PrintAsync();
    }

    [RelayCommand]
    private void Back() => _navigation.NavigateToStart();
}
