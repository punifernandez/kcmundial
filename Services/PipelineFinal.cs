using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using System.Windows.Media.Imaging;
using KCMundial.Services;

namespace KCMundial.Services
{
    /// <summary>
    /// Pipeline de calidad para foto final: alpha continuo + postprocess completo
    /// SIEMPRE corre en background thread - nunca bloquea el hilo UI
    /// </summary>
    public class PipelineFinal : IPipelineFinal
    {
        private readonly BackgroundSegmentationService _backgroundSegmentationService;
        private readonly AlphaMattePostProcessor _alphaPostProcessor;
        private readonly PersonSegmentationService _segmentationService;

        public PipelineFinal(
            BackgroundSegmentationService backgroundSegmentationService,
            AlphaMattePostProcessor alphaPostProcessor,
            PersonSegmentationService segmentationService)
        {
            _backgroundSegmentationService = backgroundSegmentationService ?? throw new ArgumentNullException(nameof(backgroundSegmentationService));
            _alphaPostProcessor = alphaPostProcessor ?? throw new ArgumentNullException(nameof(alphaPostProcessor));
            _segmentationService = segmentationService ?? throw new ArgumentNullException(nameof(segmentationService));
        }

        /// <summary>
        /// Procesa un still frame para foto final (alta calidad)
        /// Retorna rutas de archivos PNG con alpha y cutout
        /// SIEMPRE corre en background thread - nunca bloquea el hilo UI
        /// </summary>
        public async Task<(string? alphaPath, string? cutoutPath)> ProcessFrameForFinal(
            BitmapSource still,
            string outputFolder,
            CancellationToken cancellationToken = default)
        {
            if (still == null)
            {
                Debug.WriteLine("[PipelineFinal] INVALID_INPUT: still is null");
                return (null, null);
            }

            if (string.IsNullOrEmpty(outputFolder))
            {
                Debug.WriteLine("[PipelineFinal] INVALID_INPUT: outputFolder is null or empty");
                return (null, null);
            }

            var sw = Stopwatch.StartNew();
            var initialThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
            CrashLogger.Log($"[PipelineFinal] ProcessFrameForFinal START - InitialThreadId: {initialThreadId}");

            try
            {
                // Asegurar que estamos en background thread
                await Task.Yield();
                var backgroundThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                CrashLogger.Log($"[PipelineFinal] After Task.Yield - BackgroundThreadId: {backgroundThreadId}");
                cancellationToken.ThrowIfCancellationRequested();

                // 1. Convertir BitmapSource a SKBitmap (background thread)
                var swConvert = Stopwatch.StartNew();
                CrashLogger.Log($"[PipelineFinal] CONVERT_START - ThreadId: {backgroundThreadId}");
                var originalBitmap = BitmapSourceToSKBitmap(still);
                swConvert.Stop();
                CrashLogger.Log($"[PipelineFinal] CONVERT_DONE - ThreadId: {backgroundThreadId}, Time: {swConvert.ElapsedMilliseconds}ms");
                
                if (originalBitmap == null || originalBitmap.IsNull)
                {
                    CrashLogger.Log($"[PipelineFinal] CONVERT_FAILED - ThreadId: {backgroundThreadId}");
                    return (null, null);
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 2. Generar máscara alpha en alta resolución (720px) - calidad (background thread)
                var swInference = Stopwatch.StartNew();
                CrashLogger.Log($"[PipelineFinal] INFERENCE_START - ThreadId: {backgroundThreadId}");
                var alphaMask = await Task.Run(() =>
                {
                    var inferenceThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    CrashLogger.Log($"[PipelineFinal] INFERENCE_RUNNING - ThreadId: {inferenceThreadId}");
                    return _backgroundSegmentationService.GenerateAlphaMatteFromBitmap(originalBitmap, targetWidth: 720);
                }, cancellationToken);
                swInference.Stop();
                CrashLogger.Log($"[PipelineFinal] INFERENCE_DONE - ThreadId: {backgroundThreadId}, Time: {swInference.ElapsedMilliseconds}ms");

                if (alphaMask == null || alphaMask.IsNull)
                {
                    originalBitmap.Dispose();
                    CrashLogger.Log($"[PipelineFinal] INFERENCE_FAILED - ThreadId: {backgroundThreadId}");
                    return (null, null);
                }
                CrashLogger.Log($"[PipelineFinal] INFERENCE_OK - {alphaMask.Width}x{alphaMask.Height}");

                cancellationToken.ThrowIfCancellationRequested();

                // 3. Postprocesar con configuración completa (final) (background thread)
                var swPostprocess = Stopwatch.StartNew();
                CrashLogger.Log($"[PipelineFinal] POSTPROCESS_START - ThreadId: {backgroundThreadId}");
                var processedMask = await Task.Run(() =>
                {
                    var postprocessThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    CrashLogger.Log($"[PipelineFinal] POSTPROCESS_RUNNING - ThreadId: {postprocessThreadId}");
                    return _alphaPostProcessor.ProcessForFinal(alphaMask);
                }, cancellationToken);
                swPostprocess.Stop();
                CrashLogger.Log($"[PipelineFinal] POSTPROCESS_DONE - ThreadId: {backgroundThreadId}, Time: {swPostprocess.ElapsedMilliseconds}ms");

                if (processedMask == null || processedMask.IsNull)
                {
                    alphaMask.Dispose();
                    originalBitmap.Dispose();
                    CrashLogger.Log($"[PipelineFinal] POSTPROCESS_FAILED - ThreadId: {backgroundThreadId}");
                    return (null, null);
                }
                CrashLogger.Log($"[PipelineFinal] POSTPROCESS_OK");

                cancellationToken.ThrowIfCancellationRequested();

                // 4. Redimensionar máscara al tamaño original si es necesario (background thread)
                SKBitmap? finalMask = null;
                if (processedMask.Width != originalBitmap.Width || processedMask.Height != originalBitmap.Height)
                {
                    var maskInfo = new SKImageInfo(originalBitmap.Width, originalBitmap.Height, SKColorType.Alpha8, SKAlphaType.Opaque);
                    finalMask = await Task.Run(() =>
                    {
                        var resizeThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                        CrashLogger.Log($"[PipelineFinal] RESIZE_RUNNING - ThreadId: {resizeThreadId}");
                        return processedMask.Resize(maskInfo, SKFilterQuality.High);
                    }, cancellationToken);
                    processedMask.Dispose();
                }
                else
                {
                    finalMask = processedMask;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 5. Aplicar máscara para generar cutout (background thread)
                var swApplyMask = Stopwatch.StartNew();
                CrashLogger.Log($"[PipelineFinal] APPLY_MASK_START - ThreadId: {backgroundThreadId}");
                var cutoutBitmap = await Task.Run(() =>
                {
                    var applyMaskThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    CrashLogger.Log($"[PipelineFinal] APPLY_MASK_RUNNING - ThreadId: {applyMaskThreadId}");
                    return _segmentationService.ApplyMask(originalBitmap, finalMask);
                }, cancellationToken);
                swApplyMask.Stop();
                CrashLogger.Log($"[PipelineFinal] APPLY_MASK_DONE - ThreadId: {backgroundThreadId}, Time: {swApplyMask.ElapsedMilliseconds}ms");

                if (cutoutBitmap == null || cutoutBitmap.IsNull)
                {
                    finalMask?.Dispose();
                    alphaMask.Dispose();
                    originalBitmap.Dispose();
                    CrashLogger.Log($"[PipelineFinal] APPLY_MASK_FAILED - ThreadId: {backgroundThreadId}");
                    return (null, null);
                }
                CrashLogger.Log($"[PipelineFinal] APPLY_MASK_OK");

                cancellationToken.ThrowIfCancellationRequested();

                // 6. Asegurar que la carpeta de salida existe
                Directory.CreateDirectory(outputFolder);

                // 7. Guardar alpha mask como PNG (background thread)
                var swSaveAlpha = Stopwatch.StartNew();
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var alphaPath = Path.Combine(outputFolder, $"alpha_{timestamp}.png");
                CrashLogger.Log($"[PipelineFinal] SAVE_ALPHA_START - ThreadId: {backgroundThreadId}, Path: {alphaPath}");

                await Task.Run(() =>
                {
                    var saveThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    CrashLogger.Log($"[PipelineFinal] SAVE_ALPHA_RUNNING - ThreadId: {saveThreadId}");
                    using (var alphaImage = SKImage.FromBitmap(finalMask))
                    using (var alphaData = alphaImage.Encode(SKEncodedImageFormat.Png, 100))
                    using (var alphaStream = File.Create(alphaPath))
                    {
                        alphaData.SaveTo(alphaStream);
                    }
                }, cancellationToken);
                swSaveAlpha.Stop();
                CrashLogger.Log($"[PipelineFinal] SAVE_ALPHA_DONE - ThreadId: {backgroundThreadId}, Time: {swSaveAlpha.ElapsedMilliseconds}ms");

                cancellationToken.ThrowIfCancellationRequested();

                // 8. Guardar cutout como PNG con alpha (background thread)
                var swSaveCutout = Stopwatch.StartNew();
                var cutoutPath = Path.Combine(outputFolder, $"cutout_{timestamp}.png");
                CrashLogger.Log($"[PipelineFinal] SAVE_CUTOUT_START - ThreadId: {backgroundThreadId}, Path: {cutoutPath}");

                await Task.Run(() =>
                {
                    var saveThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                    CrashLogger.Log($"[PipelineFinal] SAVE_CUTOUT_RUNNING - ThreadId: {saveThreadId}");
                    using (var cutoutImage = SKImage.FromBitmap(cutoutBitmap))
                    using (var cutoutData = cutoutImage.Encode(SKEncodedImageFormat.Png, 100))
                    using (var cutoutStream = File.Create(cutoutPath))
                    {
                        cutoutData.SaveTo(cutoutStream);
                    }
                }, cancellationToken);
                swSaveCutout.Stop();
                CrashLogger.Log($"[PipelineFinal] SAVE_CUTOUT_DONE - ThreadId: {backgroundThreadId}, Time: {swSaveCutout.ElapsedMilliseconds}ms");

                // Limpiar
                cutoutBitmap.Dispose();
                finalMask?.Dispose();
                alphaMask.Dispose();
                originalBitmap.Dispose();

                sw.Stop();
                CrashLogger.Log($"[PipelineFinal] ProcessFrameForFinal SUCCESS - ThreadId: {backgroundThreadId}, TotalTime: {sw.ElapsedMilliseconds}ms");

                return (alphaPath, cutoutPath);
            }
            catch (OperationCanceledException)
            {
                var cancelThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                CrashLogger.Log($"[PipelineFinal] CANCELLED - ThreadId: {cancelThreadId}");
                return (null, null);
            }
            catch (Exception ex)
            {
                var errorThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                CrashLogger.Log($"[PipelineFinal] ERROR - ThreadId: {errorThreadId}, Message: {ex.Message}", ex);
                return (null, null);
            }
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
    }
}

