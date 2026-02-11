using System;
using System.Diagnostics;
using SkiaSharp;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KCMundial.Services;

namespace KCMundial.Services
{
    /// <summary>
    /// Pipeline rápido para preview: baja resolución + postprocess mínimo
    /// Optimizado para velocidad - NO bloquea el hilo UI
    /// Throttle: máximo 8-10 FPS (120ms mínimo entre frames procesados)
    /// </summary>
    public class PipelinePreview : IPipelinePreview
    {
        private readonly BackgroundSegmentationService _backgroundSegmentationService;
        private readonly AlphaMattePostProcessor _alphaPostProcessor;
        private readonly PersonSegmentationService? _segmentationService;
        
        // Throttle: mínimo 120ms entre frames procesados (máximo ~8 FPS)
        private readonly TimeSpan _minProcessingInterval = TimeSpan.FromMilliseconds(120);
        private DateTime _lastProcessedTime = DateTime.MinValue;
        private readonly object _throttleLock = new object();
        
        // Target width para downscale antes de inferencia (mantiene aspect ratio)
        private const int TARGET_INFERENCE_WIDTH = 320;
        
        // FPS tracking
        private int _processedFrames = 0;
        private DateTime _fpsStartTime = DateTime.Now;
        private readonly TimeSpan _fpsLogInterval = TimeSpan.FromSeconds(2);

        public PipelinePreview(
            BackgroundSegmentationService backgroundSegmentationService,
            AlphaMattePostProcessor alphaPostProcessor,
            PersonSegmentationService? segmentationService = null)
        {
            _backgroundSegmentationService = backgroundSegmentationService ?? throw new ArgumentNullException(nameof(backgroundSegmentationService));
            _alphaPostProcessor = alphaPostProcessor ?? throw new ArgumentNullException(nameof(alphaPostProcessor));
            _segmentationService = segmentationService;
        }

        /// <summary>
        /// Procesa un frame para preview rápido (baja resolución + postprocess mínimo)
        /// Retorna bitmap preview procesado o null si falla o si no pasó el throttle (120ms)
        /// GARANTIZA que todo el procesamiento pesado corre en background thread
        /// </summary>
        public BitmapSource? ProcessFrameForPreview(BitmapSource frame)
        {
            if (frame == null)
                return null;

            var threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            CrashLogger.Log($"[PipelinePreview] ProcessFrameForPreview START - ThreadId: {threadId}");

            // Throttle: verificar si pasó el tiempo mínimo desde el último procesamiento
            lock (_throttleLock)
            {
                var now = DateTime.Now;
                if (now - _lastProcessedTime < _minProcessingInterval)
                {
                    // No procesar - devolver null para mostrar RAW
                    CrashLogger.Log($"[PipelinePreview] THROTTLED - ThreadId: {threadId}");
                    return null;
                }
                _lastProcessedTime = now;
            }

            var swTotal = Stopwatch.StartNew();
            var swConvert = Stopwatch.StartNew();
            var swInference = Stopwatch.StartNew();
            var swPostprocess = Stopwatch.StartNew();
            var swApplyMask = Stopwatch.StartNew();
            var swConvertBack = Stopwatch.StartNew();

            try
            {
                var width = frame.PixelWidth;
                var height = frame.PixelHeight;

                // CONVERSIÓN: BitmapSource -> SKBitmap (background thread)
                swConvert.Restart();
                CrashLogger.Log($"[PipelinePreview] CONVERT_START - ThreadId: {threadId}");
                var originalBitmap = BitmapSourceToSKBitmap(frame);
                swConvert.Stop();
                CrashLogger.Log($"[PipelinePreview] CONVERT_DONE - ThreadId: {threadId}, Time: {swConvert.ElapsedMilliseconds}ms");
                
                if (originalBitmap == null || originalBitmap.IsNull)
                {
                    CrashLogger.Log($"[PipelinePreview] CONVERT_FAILED - ThreadId: {threadId}");
                    return null;
                }

                // INFERENCIA: Generar máscara alpha en resolución reducida (background thread)
                // GenerateAlphaMatte ya hace downscale internamente a TARGET_INFERENCE_WIDTH si es necesario
                swInference.Restart();
                CrashLogger.Log($"[PipelinePreview] INFERENCE_START - ThreadId: {threadId}");
                var alphaMask = _backgroundSegmentationService.GenerateAlphaMatte(frame, targetWidth: TARGET_INFERENCE_WIDTH);
                swInference.Stop();
                CrashLogger.Log($"[PipelinePreview] INFERENCE_DONE - ThreadId: {threadId}, Time: {swInference.ElapsedMilliseconds}ms");
                
                if (alphaMask == null || alphaMask.IsNull)
                {
                    originalBitmap.Dispose();
                    CrashLogger.Log($"[PipelinePreview] INFERENCE_FAILED - ThreadId: {threadId}");
                    return null;
                }

                // POSTPROCESS: Postprocesar máscara (background thread)
                swPostprocess.Restart();
                CrashLogger.Log($"[PipelinePreview] POSTPROCESS_START - ThreadId: {threadId}");
                var processedMask = _alphaPostProcessor.ProcessForPreview(alphaMask);
                swPostprocess.Stop();
                CrashLogger.Log($"[PipelinePreview] POSTPROCESS_DONE - ThreadId: {threadId}, Time: {swPostprocess.ElapsedMilliseconds}ms");
                
                if (processedMask == null || processedMask.IsNull)
                {
                    alphaMask.Dispose();
                    originalBitmap.Dispose();
                    CrashLogger.Log($"[PipelinePreview] POSTPROCESS_FAILED - ThreadId: {threadId}");
                    return null;
                }

                // UPSAMPLE: Redimensionar máscara al tamaño original (background thread)
                CrashLogger.Log($"[PipelinePreview] UPSAMPLE_START - ThreadId: {threadId}");
                var maskInfo = new SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Opaque);
                var finalMask = processedMask.Resize(maskInfo, SKFilterQuality.Medium); // Medium = bilinear
                processedMask.Dispose();
                alphaMask.Dispose();
                CrashLogger.Log($"[PipelinePreview] UPSAMPLE_DONE - ThreadId: {threadId}");

                // APPLY MASK: Aplicar máscara al frame original (background thread)
                swApplyMask.Restart();
                CrashLogger.Log($"[PipelinePreview] APPLY_MASK_START - ThreadId: {threadId}");
                SKBitmap? resultBitmap = null;
                if (_segmentationService != null)
                {
                    resultBitmap = _segmentationService.ApplyMask(originalBitmap, finalMask);
                }
                else
                {
                    // Fallback: aplicar máscara manualmente
                    resultBitmap = ApplyMaskManually(originalBitmap, finalMask);
                }
                swApplyMask.Stop();
                CrashLogger.Log($"[PipelinePreview] APPLY_MASK_DONE - ThreadId: {threadId}, Time: {swApplyMask.ElapsedMilliseconds}ms");

                // Limpiar
                originalBitmap.Dispose();
                finalMask.Dispose();

                if (resultBitmap == null || resultBitmap.IsNull)
                {
                    CrashLogger.Log($"[PipelinePreview] APPLY_MASK_FAILED - ThreadId: {threadId}");
                    return null;
                }

                // CONVERSIÓN: SKBitmap -> BitmapSource (background thread)
                swConvertBack.Restart();
                CrashLogger.Log($"[PipelinePreview] CONVERT_BACK_START - ThreadId: {threadId}");
                var result = SKBitmapToBitmapSource(resultBitmap);
                swConvertBack.Stop();
                CrashLogger.Log($"[PipelinePreview] CONVERT_BACK_DONE - ThreadId: {threadId}, Time: {swConvertBack.ElapsedMilliseconds}ms");
                
                resultBitmap.Dispose();

                swTotal.Stop();
                
                // Logs de performance
                _processedFrames++;
                var elapsed = (DateTime.Now - _fpsStartTime).TotalSeconds;
                if (elapsed >= _fpsLogInterval.TotalSeconds)
                {
                    var fps = _processedFrames / elapsed;
                    CrashLogger.Log($"PreviewFPS_CUTOUT: {fps:F1} fps");
                    CrashLogger.Log($"Tiempo conversión (WPF->Skia): {swConvert.ElapsedMilliseconds}ms");
                    CrashLogger.Log($"Tiempo inferencia: {swInference.ElapsedMilliseconds}ms");
                    CrashLogger.Log($"Tiempo postprocess: {swPostprocess.ElapsedMilliseconds}ms");
                    CrashLogger.Log($"Tiempo applymask: {swApplyMask.ElapsedMilliseconds}ms");
                    CrashLogger.Log($"Tiempo conversión (Skia->WPF): {swConvertBack.ElapsedMilliseconds}ms");
                    CrashLogger.Log($"Tiempo total preview: {swTotal.ElapsedMilliseconds}ms");
                    
                    _processedFrames = 0;
                    _fpsStartTime = DateTime.Now;
                }

                CrashLogger.Log($"[PipelinePreview] ProcessFrameForPreview SUCCESS - ThreadId: {threadId}, TotalTime: {swTotal.ElapsedMilliseconds}ms");
                return result;
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"[PipelinePreview] ERROR - ThreadId: {threadId}, Message: {ex.Message}", ex);
                return null;
            }
        }

        private SKBitmap ApplyMaskManually(SKBitmap original, SKBitmap mask)
        {
            var result = new SKBitmap(original.Width, original.Height, SKColorType.Rgba8888, SKAlphaType.Premul);

            unsafe
            {
                var originalPtr = (uint*)original.GetPixels();
                var maskPtr = (byte*)mask.GetPixels();
                var resultPtr = (uint*)result.GetPixels();

                var originalStride = original.RowBytes / 4;
                var maskStride = mask.RowBytes;
                var resultStride = result.RowBytes / 4;

                for (int y = 0; y < original.Height; y++)
                {
                    for (int x = 0; x < original.Width; x++)
                    {
                        uint pixel = originalPtr[y * originalStride + x];
                        byte alpha = maskPtr[y * maskStride + x];

                        byte r = (byte)(pixel & 0xFF);
                        byte g = (byte)((pixel >> 8) & 0xFF);
                        byte b = (byte)((pixel >> 16) & 0xFF);
                        byte originalAlpha = (byte)((pixel >> 24) & 0xFF);

                        // Aplicar máscara al alpha
                        byte finalAlpha = (byte)((originalAlpha * alpha) / 255);

                        resultPtr[y * resultStride + x] = (uint)((finalAlpha << 24) | (b << 16) | (g << 8) | r);
                    }
                }
            }

            return result;
        }

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

        private BitmapSource? SKBitmapToBitmapSource(SKBitmap bitmap)
        {
            if (bitmap == null || bitmap.IsNull)
                return null;

            try
            {
                var info = bitmap.Info;
                var stride = info.RowBytes;
                var size = stride * info.Height;
                var pixels = new byte[size];
                unsafe
                {
                    var ptr = (byte*)bitmap.GetPixels();
                    System.Runtime.InteropServices.Marshal.Copy(new IntPtr(ptr), pixels, 0, size);
                }

                var bitmapSource = BitmapSource.Create(
                    info.Width,
                    info.Height,
                    96, 96,
                    PixelFormats.Bgra32,
                    null,
                    pixels,
                    stride);

                bitmapSource.Freeze();
                return bitmapSource;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PipelinePreview] Error convirtiendo SKBitmap a BitmapSource: {ex.Message}");
                return null;
            }
        }
    }
}

