using System.Runtime.InteropServices;

namespace KCMundial.App.Services;

/// <summary>Obtiene los bounds de cada monitor sin depender de System.Windows.Forms.</summary>
public static class ScreenHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);

    public record MonitorBounds(double Left, double Top, double Width, double Height);

    public static IReadOnlyList<MonitorBounds> GetAllMonitors()
    {
        var list = new List<MonitorBounds>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);
        return list;

        bool Callback(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
        {
            list.Add(new MonitorBounds(lprcMonitor.Left, lprcMonitor.Top, lprcMonitor.Width, lprcMonitor.Height));
            return true;
        }
    }
}
