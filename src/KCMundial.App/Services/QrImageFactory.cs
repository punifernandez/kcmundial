using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace KCMundial.App.Services;

public static class QrImageFactory
{
    public static BitmapSource Create(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        var png = code.GetGraphic(12, new byte[] { 12, 30, 63 }, new byte[] { 255, 255, 255 }, drawQuietZones: true);
        using var ms = new MemoryStream(png);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Carga un JPEG sin bloquear el archivo, opcionalmente reducido.</summary>
    public static BitmapSource? LoadImage(string path, int decodeWidth = 0)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
