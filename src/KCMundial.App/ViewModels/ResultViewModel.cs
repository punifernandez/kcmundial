using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KCMundial.App.Services;

namespace KCMundial.App.ViewModels;

public enum PrintState { Idle, Printing, Done, Error }

/// <summary>Estado de impresión compartido por la pantalla de resultado y el detalle de galería.</summary>
public abstract partial class PrintableFiguritaViewModel : ObservableObject
{
    private readonly PhotoPrinter _printer;
    private readonly int _copies;

    protected PrintableFiguritaViewModel(string figuritaId, string displayPath, string printPath, PhotoPrinter printer, int copies)
    {
        FiguritaId = figuritaId;
        PrintPath = printPath;
        _printer = printer;
        _copies = copies;
        FiguritaImage = QrImageFactory.LoadImage(displayPath);
    }

    public string FiguritaId { get; }
    protected string PrintPath { get; }

    [ObservableProperty]
    private BitmapSource? _figuritaImage;

    [ObservableProperty]
    private BitmapSource? _qrImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPrinting))]
    [NotifyPropertyChangedFor(nameof(IsPrintError))]
    [NotifyPropertyChangedFor(nameof(IsPrintDone))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private PrintState _printState = PrintState.Idle;

    [ObservableProperty]
    private string? _printStatusMessage;

    public bool IsPrinting => PrintState == PrintState.Printing;
    public bool IsPrintError => PrintState == PrintState.Error;
    public bool IsPrintDone => PrintState == PrintState.Done;

    public void SetQrUrl(string url) => QrImage = QrImageFactory.Create(url);

    private bool CanPrint() => PrintState != PrintState.Printing;

    [RelayCommand(CanExecute = nameof(CanPrint))]
    protected async Task PrintAsync()
    {
        PrintState = PrintState.Printing;
        PrintStatusMessage = "Imprimiendo…";
        OnPrintStarted();
        try
        {
            var outcome = await _printer.PrintAsync(PrintPath, _copies);
            PrintState = outcome.Success ? PrintState.Done : PrintState.Error;
            PrintStatusMessage = outcome.Message;
        }
        catch (Exception ex)
        {
            PrintState = PrintState.Error;
            PrintStatusMessage = $"No se pudo imprimir: {ex.Message}";
        }
    }

    protected virtual void OnPrintStarted() { }
}

public partial class ResultViewModel : PrintableFiguritaViewModel
{
    private readonly INavigationService _navigation;
    private readonly DispatcherTimer _autoReturnTimer;
    private readonly TimeSpan _autoReturnAfter;
    private DateTime _autoReturnStart;

    /// <summary>1 → 0 a medida que se acerca la vuelta automática al inicio.</summary>
    [ObservableProperty]
    private double _autoReturnRemaining = 1;

    public ResultViewModel(string figuritaId, string displayPath, string printPath, string? qrUrl, INavigationService navigation,
        PhotoPrinter printer, AppSettings settings)
        : base(figuritaId, displayPath, printPath, printer, settings.PrintCopies)
    {
        _navigation = navigation;
        _autoReturnAfter = TimeSpan.FromSeconds(settings.ResultAutoReturnSeconds);
        if (qrUrl != null) SetQrUrl(qrUrl);

        _autoReturnTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _autoReturnTimer.Tick += (_, _) =>
        {
            var left = 1 - (DateTime.UtcNow - _autoReturnStart) / _autoReturnAfter;
            AutoReturnRemaining = Math.Max(0, left);
            if (left <= 0) Back();
        };
        RestartAutoReturn();

        if (settings.AutoPrint)
            _ = PrintAsync();
    }

    private void RestartAutoReturn()
    {
        _autoReturnStart = DateTime.UtcNow;
        AutoReturnRemaining = 1;
        _autoReturnTimer.Start();
    }

    // Reimprimir cuenta como actividad: no volver al inicio en medio de eso.
    protected override void OnPrintStarted() => RestartAutoReturn();

    [RelayCommand]
    private void Back()
    {
        _autoReturnTimer.Stop();
        _navigation.NavigateToMain();
    }
}
