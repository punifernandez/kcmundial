using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KCMundial.App.Services;

/// <summary>Monitores (en píxeles físicos) y ubicación de ventanas, sin depender de System.Windows.Forms.</summary>
public static class ScreenHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const uint MONITORINFOF_PRIMARY = 1;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public record MonitorBounds(int Left, int Top, int Width, int Height, bool IsPrimary, string DeviceName);

    /// <summary>Monitores con el principal primero (EnumDisplayMonitors no garantiza orden).</summary>
    public static IReadOnlyList<MonitorBounds> GetAllMonitors()
    {
        var list = new List<MonitorBounds>();
        EnumMonitorsDelegate callback = (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcMonitor;
                list.Add(new MonitorBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0, info.szDevice));
            }
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Left).ThenBy(m => m.Top).ToList();
    }

    /// <summary>
    /// Pone la ventana a pantalla completa en el monitor indicado. Se posiciona en píxeles físicos
    /// (así no importa la escala de cada monitor) y después se maximiza sobre ese monitor.
    /// </summary>
    public static void ShowFullScreenOn(Window window, MonitorBounds monitor)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.WindowState = WindowState.Normal;
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, monitor.Left, monitor.Top, Math.Max(200, monitor.Width / 2), Math.Max(200, monitor.Height / 2), SWP_NOZORDER | SWP_NOACTIVATE);
        };
        window.Loaded += (_, _) => window.WindowState = WindowState.Maximized;
        window.Show();
    }
}
