using System;
using System.IO;
using SkiaSharp;

namespace KCMundial.Services
{
    /// <summary>
    /// Resultado de background removal con metadata
    /// </summary>
    public class BackgroundRemovalResult : IDisposable
    {
        /// <summary>
        /// Foreground con alpha (RGBA)
        /// </summary>
        public SKBitmap? ForegroundRgba { get; set; }

        /// <summary>
        /// Máscara alpha (Alpha8, 0-255)
        /// </summary>
        public SKBitmap? AlphaMask { get; set; }

        /// <summary>
        /// Confidence del resultado (0..1)
        /// </summary>
        public float Confidence { get; set; }

        /// <summary>
        /// Tiempo de procesamiento total (ms)
        /// </summary>
        public long ProcessingTimeMs { get; set; }

        /// <summary>
        /// Tiempo de segmentación (ms)
        /// </summary>
        public long SegmentationTimeMs { get; set; }

        /// <summary>
        /// Tiempo de post-procesamiento (ms)
        /// </summary>
        public long PostProcessingTimeMs { get; set; }

        /// <summary>
        /// Si se usó fallback remoto (Remove.bg)
        /// </summary>
        public bool UsedRemoteFallback { get; set; }

        /// <summary>
        /// Si el resultado es válido (confidence >= threshold)
        /// </summary>
        public bool IsValid => Confidence >= 0.0f;

        /// <summary>
        /// Guarda el foreground como PNG
        /// </summary>
        public void SaveForegroundAsPng(string outputPath)
        {
            if (ForegroundRgba == null || ForegroundRgba.IsNull)
                throw new InvalidOperationException("ForegroundRgba is null");

            using (var image = SKImage.FromBitmap(ForegroundRgba))
            {
                using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                {
                    using (var stream = File.Create(outputPath))
                    {
                        data.SaveTo(stream);
                    }
                }
            }
        }

        /// <summary>
        /// Guarda la máscara alpha como PNG
        /// </summary>
        public void SaveAlphaMaskAsPng(string outputPath)
        {
            if (AlphaMask == null || AlphaMask.IsNull)
                throw new InvalidOperationException("AlphaMask is null");

            // Convertir Alpha8 a RGBA para guardar
            var rgbaMask = new SKBitmap(AlphaMask.Width, AlphaMask.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            unsafe
            {
                var alphaPtr = (byte*)AlphaMask.GetPixels();
                var rgbaPtr = (uint*)rgbaMask.GetPixels();
                var alphaStride = AlphaMask.RowBytes;
                var rgbaStride = rgbaMask.RowBytes / 4;

                for (int y = 0; y < AlphaMask.Height; y++)
                {
                    for (int x = 0; x < AlphaMask.Width; x++)
                    {
                        byte alpha = alphaPtr[y * alphaStride + x];
                        rgbaPtr[y * rgbaStride + x] = (uint)((alpha << 24) | (alpha << 16) | (alpha << 8) | alpha);
                    }
                }
            }

            using (var image = SKImage.FromBitmap(rgbaMask))
            {
                using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                {
                    using (var stream = File.Create(outputPath))
                    {
                        data.SaveTo(stream);
                    }
                }
            }

            rgbaMask.Dispose();
        }

        /// <summary>
        /// Calcula estadísticas de la máscara alpha
        /// </summary>
        public AlphaStatistics CalculateAlphaStatistics()
        {
            if (AlphaMask == null || AlphaMask.IsNull)
                return new AlphaStatistics();

            var stats = new AlphaStatistics();
            long totalPixels = AlphaMask.Width * AlphaMask.Height;
            long sum = 0;
            int min = 255, max = 0;
            int countLow = 0, countMid = 0, countHigh = 0;

            unsafe
            {
                var ptr = (byte*)AlphaMask.GetPixels();
                var stride = AlphaMask.RowBytes;

                for (int y = 0; y < AlphaMask.Height; y++)
                {
                    for (int x = 0; x < AlphaMask.Width; x++)
                    {
                        byte alpha = ptr[y * stride + x];
                        sum += alpha;
                        if (alpha < min) min = alpha;
                        if (alpha > max) max = alpha;

                        if (alpha <= 5)
                            countLow++;
                        else if (alpha < 250)
                            countMid++;
                        else
                            countHigh++;
                    }
                }
            }

            stats.Min = min;
            stats.Max = max;
            stats.Mean = totalPixels > 0 ? (float)sum / totalPixels : 0.0f;
            stats.PercentLow = totalPixels > 0 ? (float)countLow / totalPixels * 100.0f : 0.0f;
            stats.PercentMid = totalPixels > 0 ? (float)countMid / totalPixels * 100.0f : 0.0f;
            stats.PercentHigh = totalPixels > 0 ? (float)countHigh / totalPixels * 100.0f : 0.0f;

            return stats;
        }

        public void Dispose()
        {
            ForegroundRgba?.Dispose();
            AlphaMask?.Dispose();
        }
    }

    /// <summary>
    /// Estadísticas de la máscara alpha
    /// </summary>
    public class AlphaStatistics
    {
        public int Min { get; set; }
        public int Max { get; set; }
        public float Mean { get; set; }
        public float PercentLow { get; set; }  // (0, 5]
        public float PercentMid { get; set; }   // (5, 250)
        public float PercentHigh { get; set; } // [250, 255]
    }
}

