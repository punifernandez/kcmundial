using System;
using System.Diagnostics;
using SkiaSharp;
using System.Windows.Media.Imaging;
using System.IO;
using System.Linq;

namespace KCMundial.Services
{
    /// <summary>
    /// Servicio para generar máscaras alpha de personas (para reemplazo de fondo)
    /// </summary>
    public class BackgroundSegmentationService
    {
        private readonly PersonSegmentationService _segmentationService;
        private bool _isInitialized = false;

        public BackgroundSegmentationService(PersonSegmentationService segmentationService)
        {
            _segmentationService = segmentationService;
        }

        /// <summary>
        /// Inicializa el servicio de segmentación
        /// </summary>
        public bool Initialize()
        {
            if (!_segmentationService.IsInitialized)
            {
                _isInitialized = _segmentationService.Initialize();
            }
            else
            {
                _isInitialized = true;
            }
            return _isInitialized;
        }

        /// <summary>
        /// Genera una máscara alpha (0-1) de la persona desde un BitmapSource
        /// Para preview: usar tamaño reducido (ej: 320px ancho)
        /// Para final: usar tamaño completo o 720px ancho
        /// </summary>
        public SKBitmap? GenerateAlphaMatte(BitmapSource frame, int targetWidth = 320)
        {
            if (frame == null)
                return null;

            var sw = Stopwatch.StartNew();

            try
            {
                // Convertir BitmapSource a SKBitmap
                var skBitmap = BitmapSourceToSKBitmap(frame);
                if (skBitmap == null || skBitmap.IsNull)
                    return null;

                // Redimensionar para segmentación (más rápido)
                SKBitmap? resizedBitmap = null;
                if (skBitmap.Width > targetWidth)
                {
                    var scale = (float)targetWidth / skBitmap.Width;
                    var targetHeight = (int)(skBitmap.Height * scale);
                    var resizedInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                    resizedBitmap = skBitmap.Resize(resizedInfo, SKFilterQuality.Medium);
                }
                else
                {
                    resizedBitmap = skBitmap.Copy();
                }

                // Obtener imagen sin fondo (con transparencia)
                var personBitmap = _segmentationService.RemoveBackground(resizedBitmap);
                if (personBitmap == null || personBitmap.IsNull)
                {
                    resizedBitmap?.Dispose();
                    skBitmap.Dispose();
                    return null;
                }

                // Extraer canal alpha como máscara
                var mask = ExtractAlphaChannel(personBitmap);

                // Limpiar
                personBitmap.Dispose();
                if (resizedBitmap != skBitmap)
                    resizedBitmap?.Dispose();
                skBitmap.Dispose();

                sw.Stop();
                Debug.WriteLine($"[BackgroundSegmentation] Máscara generada en {sw.ElapsedMilliseconds}ms ({targetWidth}px)");

                return mask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error en GenerateAlphaMatte: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Genera máscara alpha desde SKBitmap directamente (para foto final)
        /// Usa método directo GetAlphaMatte si está disponible (alpha continuo)
        /// </summary>
        public SKBitmap? GenerateAlphaMatteFromBitmap(SKBitmap bitmap, int targetWidth = 720)
        {
            if (bitmap == null || bitmap.IsNull)
                return null;

            var sw = Stopwatch.StartNew();

            try
            {
                // Redimensionar si es necesario
                SKBitmap? resizedBitmap = null;
                if (bitmap.Width > targetWidth)
                {
                    var scale = (float)targetWidth / bitmap.Width;
                    var targetHeight = (int)(bitmap.Height * scale);
                    var resizedInfo = new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                    resizedBitmap = bitmap.Resize(resizedInfo, SKFilterQuality.High);
                }
                else
                {
                    resizedBitmap = bitmap.Copy();
                }

                // Intentar obtener alpha directamente desde el modelo (método preferido)
                var directAlpha = _segmentationService.GetAlphaMatte(resizedBitmap);
                if (directAlpha != null && !directAlpha.IsNull)
                {
                    // Redimensionar alpha al tamaño original si fue redimensionado
                    SKBitmap? finalAlpha = null;
                    if (resizedBitmap.Width != bitmap.Width || resizedBitmap.Height != bitmap.Height)
                    {
                        var alphaInfo = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Alpha8, SKAlphaType.Opaque);
                        finalAlpha = directAlpha.Resize(alphaInfo, SKFilterQuality.High);
                        directAlpha.Dispose();
                    }
                    else
                    {
                        finalAlpha = directAlpha;
                    }

                    if (resizedBitmap != bitmap)
                        resizedBitmap?.Dispose();

                    sw.Stop();
                    Debug.WriteLine($"[BackgroundSegmentation] Máscara directa generada en {sw.ElapsedMilliseconds}ms ({targetWidth}px)");
                    return finalAlpha;
                }

                // Fallback: obtener desde imagen procesada (método anterior, compatible)
                var personBitmap = _segmentationService.RemoveBackground(resizedBitmap);
                if (personBitmap == null || personBitmap.IsNull)
                {
                    if (resizedBitmap != bitmap)
                        resizedBitmap?.Dispose();
                    return null;
                }

                // Extraer canal alpha
                var mask = ExtractAlphaChannel(personBitmap);

                // Limpiar
                personBitmap.Dispose();
                if (resizedBitmap != bitmap)
                    resizedBitmap?.Dispose();

                sw.Stop();
                Debug.WriteLine($"[BackgroundSegmentation] Máscara generada (fallback) en {sw.ElapsedMilliseconds}ms ({targetWidth}px)");

                return mask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error en GenerateAlphaMatteFromBitmap: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extrae el canal alpha como máscara (SKBitmap Alpha8)
        /// </summary>
        private SKBitmap ExtractAlphaChannel(SKBitmap bitmap)
        {
            var mask = new SKBitmap(bitmap.Width, bitmap.Height, SKColorType.Alpha8, SKAlphaType.Opaque);

            unsafe
            {
                var sourcePtr = (uint*)bitmap.GetPixels();
                var maskPtr = (byte*)mask.GetPixels();
                var sourceStride = bitmap.RowBytes / 4;
                var maskStride = mask.RowBytes;

                for (int y = 0; y < bitmap.Height; y++)
                {
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        uint pixel = sourcePtr[y * sourceStride + x];
                        byte alpha = (byte)((pixel >> 24) & 0xFF);
                        maskPtr[y * maskStride + x] = alpha;
                    }
                }
            }

            return mask;
        }

        /// <summary>
        /// Convierte BitmapSource a SKBitmap
        /// </summary>
        private SKBitmap BitmapSourceToSKBitmap(BitmapSource bitmapSource)
        {
            var width = bitmapSource.PixelWidth;
            var height = bitmapSource.PixelHeight;
            var stride = width * 4; // BGRA32
            var pixels = new byte[height * stride];

            bitmapSource.CopyPixels(pixels, stride, 0);

            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);

            unsafe
            {
                var ptr = (byte*)bitmap.GetPixels();
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, new IntPtr(ptr), pixels.Length);
            }

            return bitmap;
        }
    }
}
