using System.Drawing.Printing;
using System.IO;
using System.Runtime.InteropServices;

namespace KCMundial.App.Services;

/// <summary>
/// Configuración propia del driver de una impresora (tipo de papel, calidad, sin márgenes, etc.), elegida por el
/// operador en la ventana del fabricante y guardada en un archivo (DEVMODE). Así la app imprime siempre igual,
/// sin depender de las preferencias de Windows.
/// </summary>
public static class PrinterSetup
{
    private const int DM_OUT_BUFFER = 2;
    private const int DM_IN_PROMPT = 4;
    private const int DM_IN_BUFFER = 8;
    private const int IDOK = 1;

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string printerName, out IntPtr hPrinter, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "DocumentPropertiesW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DocumentProperties(IntPtr hwnd, IntPtr hPrinter, string deviceName, IntPtr devModeOutput, IntPtr devModeInput, int mode);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    public static byte[]? Load(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch { return null; }
    }

    /// <summary>Aplica la configuración guardada al documento que se va a imprimir.</summary>
    public static void Apply(PrinterSettings printer, PageSettings page, byte[] devMode)
    {
        var h = Marshal.AllocHGlobal(devMode.Length);
        try
        {
            Marshal.Copy(devMode, 0, h, devMode.Length);
            printer.SetHdevmode(h);
            page.SetHdevmode(h);
        }
        finally
        {
            Marshal.FreeHGlobal(h);
        }
    }

    /// <summary>
    /// Abre la ventana de configuración del driver (modal a <paramref name="owner"/>), partiendo de lo guardado o de
    /// lo que tenga Windows. Si el operador acepta, lo guarda en <paramref name="path"/>. Devuelve true si se guardó.
    /// </summary>
    public static bool ShowDialogAndSave(IntPtr owner, string printerName, string path)
    {
        if (!OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
            throw new InvalidOperationException($"No se pudo abrir la impresora \"{printerName}\" (error {Marshal.GetLastWin32Error()}).");

        var input = IntPtr.Zero;
        var output = IntPtr.Zero;
        var hDefault = IntPtr.Zero;
        try
        {
            var size = DocumentProperties(owner, hPrinter, printerName, IntPtr.Zero, IntPtr.Zero, 0);
            if (size <= 0)
                throw new InvalidOperationException("El driver no devolvió su configuración.");

            var saved = Load(path);
            if (saved != null && saved.Length <= size)
            {
                input = Marshal.AllocHGlobal(size);
                Marshal.Copy(saved, 0, input, saved.Length);
            }
            else
            {
                hDefault = new PrinterSettings { PrinterName = printerName }.GetHdevmode();
                input = GlobalLock(hDefault);
            }

            output = Marshal.AllocHGlobal(size);
            var result = DocumentProperties(owner, hPrinter, printerName, output, input, DM_IN_PROMPT | DM_IN_BUFFER | DM_OUT_BUFFER);
            if (result != IDOK)
                return false;

            var bytes = new byte[size];
            Marshal.Copy(output, bytes, 0, size);
            File.WriteAllBytes(path, bytes);
            return true;
        }
        finally
        {
            if (hDefault != IntPtr.Zero)
            {
                GlobalUnlock(hDefault);
                GlobalFree(hDefault);
            }
            else if (input != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(input);
            }
            if (output != IntPtr.Zero) Marshal.FreeHGlobal(output);
            ClosePrinter(hPrinter);
        }
    }
}
