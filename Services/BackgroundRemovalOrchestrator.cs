using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace KCMundial.Services
{
    /// <summary>
    /// Orquestador principal para background removal con fallback remoto
    /// </summary>
    public class BackgroundRemovalOrchestrator : IDisposable
    {
        private readonly PersonSegmentationService _segmentationService;
        private readonly BackgroundSegmentationService _backgroundSegmentationService;
        private readonly AlphaMattePostProcessor _alphaPostProcessor;
        private readonly RemoveBgService? _removeBgService;
        private readonly BackgroundRemovalOptions _options;

        public BackgroundRemovalOrchestrator(
            PersonSegmentationService segmentationService,
            BackgroundSegmentationService backgroundSegmentationService,
            AlphaMattePostProcessor alphaPostProcessor,
            RemoveBgService? removeBgService = null,
            BackgroundRemovalOptions? options = null)
        {
            _segmentationService = segmentationService ?? throw new ArgumentNullException(nameof(segmentationService));
            _backgroundSegmentationService = backgroundSegmentationService ?? throw new ArgumentNullException(nameof(backgroundSegmentationService));
            _alphaPostProcessor = alphaPostProcessor ?? throw new ArgumentNullException(nameof(alphaPostProcessor));
            _removeBgService = removeBgService;
            _options = options ?? new BackgroundRemovalOptions();
        }

        /// <summary>
        /// API principal: remueve fondo con calidad remove.bg-like
        /// </summary>
        public async Task<BackgroundRemovalResult> RemoveBackgroundAsync(SKBitmap input, CancellationToken ct = default)
        {
            var totalSw = Stopwatch.StartNew();
            var result = new BackgroundRemovalResult();

            try
            {
                if (input == null || input.IsNull)
                    throw new ArgumentException("Input bitmap is null or invalid");

                // 1. Preprocesar: redimensionar si es necesario (mantener aspect ratio)
                SKBitmap? processedInput = PreprocessInput(input, _options.OutputMaxSide);
                if (processedInput == null)
                {
                    result.Confidence = 0.0f;
                    return result;
                }

                // 2. Intentar segmentación local
                var segmentationSw = Stopwatch.StartNew();
                SKBitmap? alphaMask = await Task.Run(() =>
                {
                    return _backgroundSegmentationService.GenerateAlphaMatteFromBitmap(
                        processedInput,
                        targetWidth: _options.OutputMaxSide
                    );
                }, ct);

                segmentationSw.Stop();
                result.SegmentationTimeMs = segmentationSw.ElapsedMilliseconds;

                if (alphaMask == null || alphaMask.IsNull)
                {
                    processedInput?.Dispose();
                    result.Confidence = 0.0f;
                    return result;
                }

                // 3. Post-procesar máscara
                var postSw = Stopwatch.StartNew();
                SKBitmap? processedMask = _alphaPostProcessor.ProcessForFinal(alphaMask);
                alphaMask.Dispose();

                if (processedMask == null || processedMask.IsNull)
                {
                    processedInput?.Dispose();
                    result.Confidence = 0.0f;
                    return result;
                }

                postSw.Stop();
                result.PostProcessingTimeMs = postSw.ElapsedMilliseconds;

                // 4. Calcular confidence
                float confidence = ConfidenceCalculator.Calculate(processedMask);
                result.Confidence = confidence;

                // 5. Si confidence bajo y fallback habilitado, usar Remove.bg
                if (confidence < _options.ConfidenceThreshold && _options.EnableRemoteFallback && _removeBgService?.IsAvailable == true)
                {
                    Debug.WriteLine($"[Orchestrator] Confidence {confidence:F2} < {_options.ConfidenceThreshold}, usando fallback remoto");
                    
                    // Guardar temporal para Remove.bg
                    string tempPath = Path.Combine(Path.GetTempPath(), $"removebg_{Guid.NewGuid()}.png");
                    try
                    {
                        using (var image = SKImage.FromBitmap(processedInput))
                        {
                            using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                            {
                                using (var stream = File.Create(tempPath))
                                {
                                    data.SaveTo(stream);
                                }
                            }
                        }

                        var remoteResult = await _removeBgService.RemoveBackgroundAsync(tempPath);
                        if (remoteResult != null && !remoteResult.IsNull)
                        {
                            // Extraer alpha de resultado remoto
                            processedMask?.Dispose();
                            processedMask = ExtractAlphaChannel(remoteResult);
                            result.UsedRemoteFallback = true;
                            result.Confidence = 1.0f; // Remove.bg = máxima confidence
                            
                            // Usar resultado remoto como foreground
                            result.ForegroundRgba = remoteResult.Copy();
                        }
                    }
                    finally
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                }

                // 6. Si no hay foreground aún, generarlo desde input + mask
                if (result.ForegroundRgba == null || result.ForegroundRgba.IsNull)
                {
                    result.ForegroundRgba = ComposeForeground(processedInput, processedMask);
                }

                result.AlphaMask = processedMask?.Copy();

                // Limpiar
                if (processedInput != input)
                    processedInput?.Dispose();

                totalSw.Stop();
                result.ProcessingTimeMs = totalSw.ElapsedMilliseconds;

                Debug.WriteLine($"[Orchestrator] Procesado en {result.ProcessingTimeMs}ms (seg: {result.SegmentationTimeMs}ms, post: {result.PostProcessingTimeMs}ms), Confidence: {result.Confidence:F2}, Remote: {result.UsedRemoteFallback}");

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Orchestrator] Error: {ex.Message}");
                result.Confidence = 0.0f;
                return result;
            }
        }

        /// <summary>
        /// Compone foreground sobre background usando alpha mask
        /// </summary>
        public SKBitmap ComposeForegroundOverBackground(SKBitmap foreground, SKBitmap alphaMask, SKBitmap background)
        {
            if (foreground == null || alphaMask == null || background == null)
                throw new ArgumentException("All bitmaps must be non-null");

            // Asegurar mismo tamaño
            int width = Math.Min(Math.Min(foreground.Width, background.Width), alphaMask.Width);
            int height = Math.Min(Math.Min(foreground.Height, background.Height), alphaMask.Height);

            SKBitmap? fgResized = null;
            SKBitmap? bgResized = null;
            SKBitmap? maskResized = null;

            try
            {
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

                            byte fgR = (byte)(fgPixel & 0xFF);
                            byte fgG = (byte)((fgPixel >> 8) & 0xFF);
                            byte fgB = (byte)((fgPixel >> 16) & 0xFF);

                            byte bgR = (byte)(bgPixel & 0xFF);
                            byte bgG = (byte)((bgPixel >> 8) & 0xFF);
                            byte bgB = (byte)((bgPixel >> 16) & 0xFF);

                            byte r = (byte)(fgR * alphaF + bgR * (1.0f - alphaF));
                            byte g = (byte)(fgG * alphaF + bgG * (1.0f - alphaF));
                            byte b = (byte)(fgB * alphaF + bgB * (1.0f - alphaF));

                            resultPtr[y * resultStride + x] = (uint)((255 << 24) | (b << 16) | (g << 8) | r);
                        }
                    }
                }

                return result;
            }
            finally
            {
                if (fgResized != foreground) fgResized?.Dispose();
                if (bgResized != background) bgResized?.Dispose();
                if (maskResized != alphaMask) maskResized?.Dispose();
            }
        }

        private SKBitmap? PreprocessInput(SKBitmap input, int maxSide)
        {
            if (input.Width <= maxSide && input.Height <= maxSide)
                return input.Copy();

            float scale = Math.Min((float)maxSide / input.Width, (float)maxSide / input.Height);
            int newWidth = (int)(input.Width * scale);
            int newHeight = (int)(input.Height * scale);

            var info = new SKImageInfo(newWidth, newHeight, input.ColorType, input.AlphaType);
            return input.Resize(info, SKFilterQuality.High);
        }

        private SKBitmap ComposeForeground(SKBitmap input, SKBitmap alphaMask)
        {
            var result = new SKBitmap(input.Width, input.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            // Asegurar mismo tamaño
            SKBitmap? maskResized = null;
            if (alphaMask.Width != input.Width || alphaMask.Height != input.Height)
            {
                var maskInfo = new SKImageInfo(input.Width, input.Height, SKColorType.Alpha8, SKAlphaType.Opaque);
                maskResized = alphaMask.Resize(maskInfo, SKFilterQuality.High);
            }
            else
            {
                maskResized = alphaMask.Copy();
            }

            unsafe
            {
                var inputPtr = (uint*)input.GetPixels();
                var maskPtr = (byte*)maskResized.GetPixels();
                var resultPtr = (uint*)result.GetPixels();

                var inputStride = input.RowBytes / 4;
                var maskStride = maskResized.RowBytes;
                var resultStride = result.RowBytes / 4;

                for (int y = 0; y < input.Height; y++)
                {
                    for (int x = 0; x < input.Width; x++)
                    {
                        uint pixel = inputPtr[y * inputStride + x];
                        byte alpha = maskPtr[y * maskStride + x];

                        byte r = (byte)(pixel & 0xFF);
                        byte g = (byte)((pixel >> 8) & 0xFF);
                        byte b = (byte)((pixel >> 16) & 0xFF);

                        resultPtr[y * resultStride + x] = (uint)((alpha << 24) | (b << 16) | (g << 8) | r);
                    }
                }
            }

            if (maskResized != alphaMask)
                maskResized?.Dispose();

            return result;
        }

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

        public void Dispose()
        {
            // Los servicios son compartidos, no los disposeamos aquí
        }
    }
}










