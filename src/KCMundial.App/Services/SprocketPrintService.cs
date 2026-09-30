using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using KCMundial.Processing;

namespace KCMundial.App.Services;

/// <summary>Envía una imagen al teléfono Android vía ADB y lanza Sprocket Relay para imprimir en HP Sprocket (2"×3").</summary>
public sealed class SprocketPrintService
{
    private const string PhoneFolder = "/storage/emulated/0/Download/SprocketBridge";
    private const string RelayComponent = "com.sprocketbridge.relay/.RelayActivity";

    /// <summary>Busca adb.exe en PATH y en %LOCALAPPDATA%\Android\Sdk\platform-tools\adb.exe.</summary>
    public static string? FindAdb()
    {
        // Buscar en PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var adb = Path.Combine(dir.Trim(), "adb.exe");
                if (File.Exists(adb))
                    return adb;
            }
        }

        // Ruta típica Android SDK
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(localAppData))
        {
            var sdkAdb = Path.Combine(localAppData, "Android", "Sdk", "platform-tools", "adb.exe");
            if (File.Exists(sdkAdb))
                return sdkAdb;
        }

        return null;
    }

    /// <summary>Ejecuta adb y devuelve la salida estándar. Si falla, exception o output contiene error.</summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAdbAsync(string adbPath, string arguments, CancellationToken cancellationToken = default)
    {
        using var process = new Process();
        process.StartInfo.FileName = adbPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.Start();

        var outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await outTask.ConfigureAwait(false);
        var stderr = await errTask.ConfigureAwait(false);
        return (process.ExitCode, stdout ?? "", stderr ?? "");
    }

    /// <summary>Comprueba si hay al menos un dispositivo en estado "device".</summary>
    public static async Task<bool> HasDeviceAsync(string adbPath, CancellationToken cancellationToken = default)
    {
        var (_, stdout, _) = await RunAdbAsync(adbPath, "devices", cancellationToken).ConfigureAwait(false);
        // Líneas como "ABC123\tdevice"
        return Regex.IsMatch(stdout, @"\tdevice\s*$", RegexOptions.Multiline);
    }

    /// <summary>Flujo completo: mkdir, push, intent. Reporta progreso y devuelve (éxito, mensaje de error si falla).</summary>
    public static async Task<(bool Success, string ErrorMessage)> SendToSprocketAsync(
        string imagePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(imagePath))
        {
            return (false, "No se encontró la imagen.");
        }

        // Encajar en 2"×3" sin recortar, con SkiaSharp para preservar color y buena calidad (636×848 px).
        progress?.Report("Preparando imagen para Sprocket (2\"×3\")...");
        var pathToSend = SprocketImagePrepare.Prepare(imagePath) ?? imagePath;
        var deleteTemp = pathToSend != imagePath;

        // Guardar copia en carpeta printed para chequear el resultado
        try
        {
            var printedDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "printed");
            Directory.CreateDirectory(printedDir);
            var baseName = Path.GetFileNameWithoutExtension(imagePath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var printedPath = Path.Combine(printedDir, $"{baseName}_{timestamp}.jpg");
            File.Copy(pathToSend, printedPath, overwrite: true);
        }
        catch { /* no fallar la impresión si falla el guardado */ }

        try
        {
            progress?.Report("Buscando ADB...");
            var adb = FindAdb();
            if (string.IsNullOrEmpty(adb))
            {
                return (false, "No se encontró ADB. Comprobá que Android SDK platform-tools esté en PATH o en %LOCALAPPDATA%\\Android\\Sdk\\platform-tools.");
            }

            progress?.Report("Comprobando dispositivo...");
            var hasDevice = await HasDeviceAsync(adb, cancellationToken).ConfigureAwait(false);
            if (!hasDevice)
            {
                return (false, "No hay dispositivo Android conectado o no está en estado \"device\". Conectá el teléfono por USB con depuración USB activada.");
            }

            var fileName = Path.GetFileName(pathToSend);
            if (string.IsNullOrEmpty(fileName))
                fileName = "figurita.jpg";

            progress?.Report("Creando carpeta en el teléfono...");
            var (exitMkdir, _, errMkdir) = await RunAdbAsync(adb, $"shell mkdir -p \"{PhoneFolder}\"", cancellationToken).ConfigureAwait(false);
            if (exitMkdir != 0 && !string.IsNullOrWhiteSpace(errMkdir))
            {
                return (false, $"Error al crear carpeta: {errMkdir.Trim()}");
            }

            progress?.Report("Enviando imagen al teléfono...");
            var remotePath = $"{PhoneFolder}/{fileName}";
            var (exitPush, _, errPush) = await RunAdbAsync(adb, $"push \"{pathToSend}\" \"{remotePath}\"", cancellationToken).ConfigureAwait(false);
            if (exitPush != 0)
            {
                return (false, string.IsNullOrWhiteSpace(errPush) ? "Error al enviar la imagen al teléfono." : errPush.Trim());
            }

            progress?.Report("Lanzando impresión...");
            var streamUri = $"file://{remotePath}";
            var intentArgs = $"shell am start -a android.intent.action.SEND -t image/jpeg --eu android.intent.extra.STREAM \"{streamUri}\" --grant-read-uri-permission -n \"{RelayComponent}\"";
            var (exitIntent, _, errIntent) = await RunAdbAsync(adb, intentArgs, cancellationToken).ConfigureAwait(false);
            if (exitIntent != 0)
            {
                return (false, string.IsNullOrWhiteSpace(errIntent) ? "No se pudo abrir Sprocket Relay. Comprobá que la app esté instalada." : errIntent.Trim());
            }

            return (true, "");
        }
        finally
        {
            if (deleteTemp && File.Exists(pathToSend))
            {
                try { File.Delete(pathToSend); } catch { /* ignore */ }
            }
        }
    }
}
