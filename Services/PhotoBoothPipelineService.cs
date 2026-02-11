using System;
using System.Diagnostics;
using SkiaSharp;
using System.Windows.Media.Imaging;

namespace KCMundial.Services
{
    /// <summary>
    /// Modo de procesamiento: Preview (rápido) o Final (alta calidad)
    /// </summary>
    public enum ProcessingMode
    {
        Preview,  // Segmentación rápida (320px) + postproceso liviano
        Final     // Segmentación mayor res (720-1080px) + postproceso más fuerte
    }

    /// <summary>
    /// Tipo de template/composición
    /// </summary>
    public enum TemplateType
    {
        Jersey,      // Pipeline A: Solo cabeza+cuello en template de camiseta
        Background   // Pipeline B: Persona completa sobre fondo
    }

    /// <summary>
    /// Servicio unificado para manejar ambos pipelines (Jersey/Card y Background Replacement)
    /// </summary>
    public class PhotoBoothPipelineService
    {
        private readonly BackgroundSegmentationService _backgroundSegmentationService;
        private readonly AlphaMattePostProcessor _alphaPostProcessor;
        private readonly BackgroundComposer _backgroundComposer;

        public PhotoBoothPipelineService(
            BackgroundSegmentationService backgroundSegmentationService,
            AlphaMattePostProcessor alphaPostProcessor,
            BackgroundComposer backgroundComposer)
        {
            _backgroundSegmentationService = backgroundSegmentationService ?? throw new ArgumentNullException(nameof(backgroundSegmentationService));
            _alphaPostProcessor = alphaPostProcessor ?? throw new ArgumentNullException(nameof(alphaPostProcessor));
            _backgroundComposer = backgroundComposer ?? throw new ArgumentNullException(nameof(backgroundComposer));
        }

        /// <summary>
        /// Obtiene máscara alpha de persona desde frame original
        /// </summary>
        public SKBitmap? GetPersonAlpha(BitmapSource frameOriginal, ProcessingMode mode)
        {
            if (frameOriginal == null)
                return null;

            try
            {
                int targetWidth = mode == ProcessingMode.Preview ? 320 : 720;
                var alphaMask = _backgroundSegmentationService.GenerateAlphaMatte(frameOriginal, targetWidth);
                
                if (alphaMask == null || alphaMask.IsNull)
                    return null;

                // Postprocesar según modo
                SKBitmap? processedMask = mode == ProcessingMode.Preview
                    ? _alphaPostProcessor.ProcessForPreview(alphaMask)
                    : _alphaPostProcessor.ProcessForFinal(alphaMask);

                alphaMask.Dispose();
                return processedMask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error en GetPersonAlpha: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Obtiene máscara alpha de persona desde SKBitmap
        /// </summary>
        public SKBitmap? GetPersonAlpha(SKBitmap frameOriginal, ProcessingMode mode)
        {
            if (frameOriginal == null || frameOriginal.IsNull)
                return null;

            try
            {
                int targetWidth = mode == ProcessingMode.Preview ? 320 : 720;
                var alphaMask = _backgroundSegmentationService.GenerateAlphaMatteFromBitmap(frameOriginal, targetWidth);
                
                if (alphaMask == null || alphaMask.IsNull)
                    return null;

                // Postprocesar según modo
                SKBitmap? processedMask = mode == ProcessingMode.Preview
                    ? _alphaPostProcessor.ProcessForPreview(alphaMask)
                    : _alphaPostProcessor.ProcessForFinal(alphaMask);

                alphaMask.Dispose();
                return processedMask;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error en GetPersonAlpha: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pipeline A: Extrae solo cabeza y cuello del frame original usando la máscara
        /// IMPORTANTE: Trabaja SOLO sobre frame original + máscara, nunca sobre imagen compuesta
        /// </summary>
        public SKBitmap? ExtractHeadAndNeck(SKBitmap frameOriginal, SKBitmap alphaPerson)
        {
            if (frameOriginal == null || frameOriginal.IsNull || alphaPerson == null || alphaPerson.IsNull)
                return null;

            try
            {
                // Asegurar que frame y máscara tengan el mismo tamaño
                SKBitmap? maskResized = null;
                if (alphaPerson.Width != frameOriginal.Width || alphaPerson.Height != frameOriginal.Height)
                {
                    var maskInfo = new SKImageInfo(frameOriginal.Width, frameOriginal.Height, SKColorType.Alpha8, SKAlphaType.Opaque);
                    maskResized = alphaPerson.Resize(maskInfo, SKFilterQuality.High);
                }
                else
                {
                    maskResized = alphaPerson.Copy();
                }

                // Encontrar límites de la persona usando la máscara
                int minX = frameOriginal.Width, minY = frameOriginal.Height;
                int maxX = 0, maxY = 0;
                bool foundPerson = false;

                unsafe
                {
                    var maskPtr = (byte*)maskResized.GetPixels();
                    var maskStride = maskResized.RowBytes;

                    for (int y = 0; y < maskResized.Height; y++)
                    {
                        for (int x = 0; x < maskResized.Width; x++)
                        {
                            byte alpha = maskPtr[y * maskStride + x];
                            if (alpha > 128) // Umbral 50%
                            {
                                foundPerson = true;
                                if (x < minX) minX = x;
                                if (x > maxX) maxX = x;
                                if (y < minY) minY = y;
                                if (y > maxY) maxY = y;
                            }
                        }
                    }
                }

                if (!foundPerson)
                {
                    if (maskResized != alphaPerson)
                        maskResized?.Dispose();
                    return null;
                }

                int personWidth = maxX - minX + 1;
                int personHeight = maxY - minY + 1;

                // Cabeza y cuello: 40% superior de la persona
                int headAndNeckHeight = (int)(personHeight * 0.40);
                int headAndNeckY = minY;
                int headAndNeckX = minX;
                int headAndNeckWidth = personWidth;

                // Asegurar límites
                if (headAndNeckY + headAndNeckHeight > frameOriginal.Height)
                    headAndNeckHeight = frameOriginal.Height - headAndNeckY;
                if (headAndNeckX + headAndNeckWidth > frameOriginal.Width)
                    headAndNeckWidth = frameOriginal.Width - headAndNeckX;

                // Crear bitmap con cabeza y cuello del frame original, aplicando máscara
                var headAndNeckBitmap = new SKBitmap(headAndNeckWidth, headAndNeckHeight, SKColorType.Rgba8888, SKAlphaType.Premul);

                unsafe
                {
                    var sourcePtr = (uint*)frameOriginal.GetPixels();
                    var maskPtr = (byte*)maskResized.GetPixels();
                    var destPtr = (uint*)headAndNeckBitmap.GetPixels();
                    var sourceStride = frameOriginal.RowBytes / 4;
                    var maskStride = maskResized.RowBytes;
                    var destStride = headAndNeckBitmap.RowBytes / 4;

                    for (int y = 0; y < headAndNeckHeight; y++)
                    {
                        for (int x = 0; x < headAndNeckWidth; x++)
                        {
                            int sourceX = headAndNeckX + x;
                            int sourceY = headAndNeckY + y;

                            if (sourceX < frameOriginal.Width && sourceY < frameOriginal.Height)
                            {
                                uint sourcePixel = sourcePtr[sourceY * sourceStride + sourceX];
                                byte maskAlpha = maskPtr[sourceY * maskStride + sourceX];

                                // Aplicar máscara al alpha
                                byte sourceA = (byte)((sourcePixel >> 24) & 0xFF);
                                byte finalAlpha = (byte)((sourceA * maskAlpha) / 255);

                                uint finalPixel = (sourcePixel & 0x00FFFFFF) | ((uint)finalAlpha << 24);
                                destPtr[y * destStride + x] = finalPixel;
                            }
                        }
                    }
                }

                if (maskResized != alphaPerson)
                    maskResized?.Dispose();

                return headAndNeckBitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error en ExtractHeadAndNeck: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pipeline B: Compone persona completa sobre background
        /// </summary>
        public SKBitmap? ComposePersonOnBackground(SKBitmap frameOriginal, SKBitmap alphaPerson, SKBitmap background)
        {
            if (frameOriginal == null || alphaPerson == null || background == null)
                return null;

            // Asegurar que máscara tenga el mismo tamaño que frame
            SKBitmap? maskResized = null;
            if (alphaPerson.Width != frameOriginal.Width || alphaPerson.Height != frameOriginal.Height)
            {
                var maskInfo = new SKImageInfo(frameOriginal.Width, frameOriginal.Height, SKColorType.Alpha8, SKAlphaType.Opaque);
                maskResized = alphaPerson.Resize(maskInfo, SKFilterQuality.High);
            }
            else
            {
                maskResized = alphaPerson.Copy();
            }

            try
            {
                // Redimensionar background al tamaño del frame
                var bgResized = background.Resize(
                    new SKImageInfo(frameOriginal.Width, frameOriginal.Height, SKColorType.Rgba8888, SKAlphaType.Opaque),
                    SKFilterQuality.High);

                // Componer
                var result = _backgroundComposer.Compose(frameOriginal, bgResized, maskResized);

                if (bgResized != background)
                    bgResized?.Dispose();

                return result;
            }
            finally
            {
                if (maskResized != alphaPerson)
                    maskResized?.Dispose();
            }
        }

        /// <summary>
        /// Pipeline A: Compone cabeza y cuello sobre template de camiseta
        /// (Esta función se delega a CollageService que ya maneja esto)
        /// </summary>
        public SKBitmap? ComposeHeadOnTemplate(SKBitmap headCrop, SKBitmap jerseyTemplate)
        {
            // Esta función se delega a CollageService.CreateStickerAsync
            // que ya maneja la composición de cabeza en template
            // Por ahora retornamos null y dejamos que CollageService lo maneje
            return null;
        }

        /// <summary>
        /// Convierte SKBitmap a BitmapSource
        /// </summary>
        public BitmapSource? SKBitmapToBitmapSource(SKBitmap bitmap)
        {
            return _backgroundComposer.SKBitmapToBitmapSource(bitmap);
        }
    }
}






