using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace KCMundial
{
    public class Program
    {
        // P/Invoke para Win32 API
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern void OutputDebugString(string lpOutputString);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(
            IntPtr hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint GENERIC_WRITE = 0x40000000;
        private const uint CREATE_ALWAYS = 2;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const uint FILE_SHARE_WRITE = 2;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        [STAThread]
        public static void Main(string[] args)
        {
            // PRIMERA LÍNEA: PROOF usando Win32 P/Invoke (ANTES de WPF)
            var buildTag = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string exePath = "UNKNOWN";
            string baseDir = AppContext.BaseDirectory;
            
            try
            {
                var p = Process.GetCurrentProcess();
                exePath = p.MainModule?.FileName ?? "UNKNOWN";
            }
            catch { }

            // OutputDebugString para Visual Studio Output/DebugView
            var debugMsg = $"ENTRYPOINT HIT {buildTag} {exePath} {baseDir}";
            OutputDebugString(debugMsg);

            // Escribir proof usando Win32 CreateFile/WriteFile (NO System.IO)
            var tempPath = Environment.GetEnvironmentVariable("TEMP") ?? Environment.GetEnvironmentVariable("TMP") ?? "C:\\Temp";
            var proofPath = $"{tempPath}\\KCMundial_PROOF_{buildTag}.txt";
            var proofContent = $"PROOF OK {buildTag}\nEXE: {exePath}\nBaseDir: {baseDir}\nTimestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\n";
            
            try
            {
                var contentBytes = Encoding.UTF8.GetBytes(proofContent);
                var hFile = CreateFile(
                    proofPath,
                    GENERIC_WRITE,
                    FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    CREATE_ALWAYS,
                    FILE_ATTRIBUTE_NORMAL,
                    IntPtr.Zero);

                if (hFile != INVALID_HANDLE_VALUE)
                {
                    WriteFile(hFile, contentBytes, (uint)contentBytes.Length, out _, IntPtr.Zero);
                    CloseHandle(hFile);
                }
            }
            catch { }

            // Fail-safe: Beep
            try
            {
                Console.Beep(1200, 300);
                System.Threading.Thread.Sleep(100);
                Console.Beep(1200, 300);
            }
            catch { }

            // MessageBox (opcional pero visible)
            MessageBox.Show(
                $"ENTRYPOINT HIT: {buildTag}\nEXE: {exePath}\nBaseDir: {baseDir}\n\nProof file: {proofPath}",
                "KC PROOF",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            // Ejecutar aplicación WPF
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
