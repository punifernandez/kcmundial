using System;
using System.Diagnostics;
using SkiaSharp;
using System.Windows.Media.Imaging;

namespace KCMundial.Services
{
    /// <summary>
    /// Compone persona (foreground) sobre background con máscara alpha
    /// </summary>
    public class BackgroundComposer
    {
        /// <summary>
        /// Compone foreground sobre background usando máscara alpha
        /// out = fg * alpha + bg * (1 - alpha)
        /// </summary>
        public SKBitmap? Compose(SKBitmap foreground, SKBitmap background, SKBitmap alphaMask)
        {
            if (foreground == null || background == null || alphaMask == null)
                return null;

            var sw = Stopwatch.StartNew();

            try
            {
                // Asegurar que todos tengan el mismo tamaño
                int width = Math.Min(Math.Min(foreground.Width, background.Width), alphaMask.Width);
                int height = Math.Min(Math.Min(foreground.Height, background.Height), alphaMask.Height);

                // Redimensionar si es necesario
                SKBitmap? fgResized = null;
                SKBitmap? bgResized = null;
                SKBitmap? maskResized = null;

                if (foreground.Width != width || foreground.Height != height)
                {
                    var fgInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    fgResized = foreground.Resize(fgInfo, SKFilterQuality.High);
                }
                else
                {
                    fgResized = foreground.Copy();
                }

                if (background.Width != width || background.Height != height)
                {
                    var bgInfo = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    bgResized = background.Resize(bgInfo, SKFilterQuality.High);
                }
                else
                {
                    bgResized = background.Copy();
                }

                if (alphaMask.Width != width || alphaMask.Height != height)
                {
                    var maskInfo = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Opaque);
                    maskResized = alphaMask.Resize(maskInfo, SKFilterQuality.Medium);
                }
                else
                {
                    maskResized = alphaMask.Copy();
                }

                // Crear resultado
                var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

                unsafe
                {
                    var fgPtr = (uint*)fgResized.GetPixels();
                    var bgPtr = (uint*)bgResized.GetPixels();
                    var maskPtr = (byte*)maskResized.GetPixels();
                    var resultPtr = (uint*)result.GetPixels();

                    var fgStride = fgResized.RowBytes / 4;
                    var bgStride = bgResized.RowBytes / 4;
                    var maskStride = maskResized.RowBytes;
                    var resultStride = result.RowBytes / 4;

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            uint fgPixel = fgPtr[y * fgStride + x];
                            uint bgPixel = bgPtr[y * bgStride + x];
                            byte alpha = maskPtr[y * maskStride + x];

                            float alphaF = alpha / 255.0f;

                            // Edge despill: solo en bordes muy suaves (alpha 0.2-0.5)
                            // Mínimo despill para mantener opacidad
                            if (alphaF > 0.2f && alphaF < 0.5f)
                            {
                                float despillStrength = 0.05f * (1.0f - Math.Abs(alphaF - 0.35f) * 3.33f); // Máximo en alpha=0.35
                                alphaF = alphaF * (1.0f - despillStrength);
                            }

                            // Extraer componentes
                            byte fgR = (byte)(fgPixel & 0xFF);
                            byte fgG = (byte)((fgPixel >> 8) & 0xFF);
                            byte fgB = (byte)((fgPixel >> 16) & 0xFF);
                            byte fgA = (byte)((fgPixel >> 24) & 0xFF);

                            byte bgR = (byte)(bgPixel & 0xFF);
                            byte bgG = (byte)((bgPixel >> 8) & 0xFF);
                            byte bgB = (byte)((bgPixel >> 16) & 0xFF);

                            // Composición: out = fg * alpha + bg * (1 - alpha)
                            // Para preview: usar alpha de máscara directamente, sin multiplicar con fgA
                            // Esto asegura máxima opacidad donde la máscara lo indica
                            float effectiveAlpha = alphaF;
                            
                            // Solo en áreas muy transparentes (alpha < 0.1) considerar el alpha del foreground
                            if (alphaF < 0.1f && fgA < 255)
                            {
                                effectiveAlpha = alphaF * (fgA / 255.0f);
                            }
                            
                            byte r = (byte)(fgR * effectiveAlpha + bgR * (1.0f - effectiveAlpha));
                            byte g = (byte)(fgG * effectiveAlpha + bgG * (1.0f - effectiveAlpha));
                            byte b = (byte)(fgB * effectiveAlpha + bgB * (1.0f - effectiveAlpha));
                            byte a = 255; // Siempre opaco en el resultado final

                            resultPtr[y * resultStride + x] = (uint)((a << 24) | (b << 16) | (g << 8) | r);
                        }
                    }
                }

                // Limpiar temporales
                if (fgResized != foreground) fgResized?.Dispose();
                if (bgResized != background) bgResized?.Dispose();
                if (maskResized != alphaMask) maskResized?.Dispose();

                sw.Stop();
                Debug.WriteLine($"[BackgroundComposer] Composición en {sw.ElapsedMilliseconds}ms ({width}x{height})");

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error en Compose: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Convierte SKBitmap a BitmapSource para WPF
        /// </summary>
        public BitmapSource? SKBitmapToBitmapSource(SKBitmap bitmap)
        {
            if (bitmap == null || bitmap.IsNull)
                return null;

            try
            {
                using (var image = SKImage.FromBitmap(bitmap))
                {
                    using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                    {
                        var bitmapImage = new BitmapImage();
                        bitmapImage.BeginInit();
                        bitmapImage.StreamSource = data.AsStream();
                        bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                        bitmapImage.EndInit();
                        bitmapImage.Freeze();
                        return bitmapImage;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error al convertir SKBitmap a BitmapSource: {ex.Message}");
                return null;
            }
        }
    }
}
