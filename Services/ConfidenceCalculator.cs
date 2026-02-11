using System;
using System.Diagnostics;
using SkiaSharp;

namespace KCMundial.Services
{
    /// <summary>
    /// Calcula confidence de una máscara alpha basado en edge quality, coverage y consistency
    /// </summary>
    public static class ConfidenceCalculator
    {
        /// <summary>
        /// Calcula confidence de una máscara (0..1)
        /// </summary>
        public static float Calculate(SKBitmap alphaMask)
        {
            if (alphaMask == null || alphaMask.IsNull || alphaMask.ColorType != SKColorType.Alpha8)
                return 0.0f;

            var sw = Stopwatch.StartNew();

            try
            {
                // 1. Edge Quality: gradiente de la máscara (bordes definidos = alta calidad)
                float edgeQuality = CalculateEdgeQuality(alphaMask);

                // 2. Coverage: área de foreground vs total (persona presente = alta coverage)
                float coverage = CalculateCoverage(alphaMask);

                // 3. Consistency: variación espacial (máscara coherente = alta consistency)
                float consistency = CalculateConsistency(alphaMask);

                // Confidence = weighted average
                // Edge quality es más importante (40%), coverage (35%), consistency (25%)
                float confidence = (edgeQuality * 0.40f) + (coverage * 0.35f) + (consistency * 0.25f);

                sw.Stop();
                Debug.WriteLine($"[ConfidenceCalculator] Edge: {edgeQuality:F2}, Coverage: {coverage:F2}, Consistency: {consistency:F2}, Final: {confidence:F2} ({sw.ElapsedMilliseconds}ms)");

                return Math.Clamp(confidence, 0.0f, 1.0f);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error calculando confidence: {ex.Message}");
                return 0.0f;
            }
        }

        /// <summary>
        /// Edge Quality: mide qué tan definidos están los bordes (gradiente alto = bueno)
        /// </summary>
        private static float CalculateEdgeQuality(SKBitmap mask)
        {
            unsafe
            {
                var ptr = (byte*)mask.GetPixels();
                var stride = mask.RowBytes;
                int edgePixels = 0;
                float totalGradient = 0.0f;

                for (int y = 1; y < mask.Height - 1; y++)
                {
                    for (int x = 1; x < mask.Width - 1; x++)
                    {
                        byte center = ptr[y * stride + x];
                        
                        // Solo considerar píxeles en transición (no completamente opacos ni transparentes)
                        if (center > 10 && center < 245)
                        {
                            // Calcular gradiente (Sobel-like)
                            int gx = Math.Abs(
                                (int)ptr[(y - 1) * stride + (x + 1)] - (int)ptr[(y - 1) * stride + (x - 1)] +
                                2 * ((int)ptr[y * stride + (x + 1)] - (int)ptr[y * stride + (x - 1)]) +
                                (int)ptr[(y + 1) * stride + (x + 1)] - (int)ptr[(y + 1) * stride + (x - 1)]
                            );

                            int gy = Math.Abs(
                                (int)ptr[(y - 1) * stride + (x - 1)] - (int)ptr[(y + 1) * stride + (x - 1)] +
                                2 * ((int)ptr[(y - 1) * stride + x] - (int)ptr[(y + 1) * stride + x]) +
                                (int)ptr[(y - 1) * stride + (x + 1)] - (int)ptr[(y + 1) * stride + (x + 1)]
                            );

                            float gradient = (float)Math.Sqrt(gx * gx + gy * gy) / 255.0f;
                            totalGradient += gradient;
                            edgePixels++;
                        }
                    }
                }

                if (edgePixels == 0)
                    return 0.5f; // Sin bordes detectados, confidence medio

                float avgGradient = totalGradient / edgePixels;
                // Normalizar: gradiente alto (> 0.3) = buena calidad
                return Math.Clamp(avgGradient / 0.3f, 0.0f, 1.0f);
            }
        }

        /// <summary>
        /// Coverage: porcentaje de píxeles con alpha > threshold
        /// </summary>
        private static float CalculateCoverage(SKBitmap mask)
        {
            unsafe
            {
                var ptr = (byte*)mask.GetPixels();
                var stride = mask.RowBytes;
                int totalPixels = mask.Width * mask.Height;
                int foregroundPixels = 0;

                for (int y = 0; y < mask.Height; y++)
                {
                    for (int x = 0; x < mask.Width; x++)
                    {
                        if (ptr[y * stride + x] > 128) // Threshold 50%
                            foregroundPixels++;
                    }
                }

                float coverage = (float)foregroundPixels / totalPixels;
                
                // Coverage ideal: 0.15-0.40 (persona centrada, no demasiado grande ni pequeña)
                if (coverage < 0.05f)
                    return coverage * 2.0f; // Penalizar si es muy pequeña
                if (coverage > 0.60f)
                    return 1.0f - (coverage - 0.60f) * 0.5f; // Penalizar si ocupa toda la imagen
                
                return Math.Clamp(coverage / 0.30f, 0.0f, 1.0f);
            }
        }

        /// <summary>
        /// Consistency: mide variación espacial (máscara coherente = alta consistency)
        /// </summary>
        private static float CalculateConsistency(SKBitmap mask)
        {
            unsafe
            {
                var ptr = (byte*)mask.GetPixels();
                var stride = mask.RowBytes;
                float totalVariance = 0.0f;
                int samples = 0;

                // Muestrear en ventanas 3x3
                for (int y = 1; y < mask.Height - 1; y += 2)
                {
                    for (int x = 1; x < mask.Width - 1; x += 2)
                    {
                        // Calcular varianza local
                        float sum = 0.0f;
                        float sumSq = 0.0f;
                        int count = 0;

                        for (int dy = -1; dy <= 1; dy++)
                        {
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                byte val = ptr[(y + dy) * stride + (x + dx)];
                                sum += val;
                                sumSq += val * val;
                                count++;
                            }
                        }

                        float mean = sum / count;
                        float variance = (sumSq / count) - (mean * mean);
                        totalVariance += variance;
                        samples++;
                    }
                }

                if (samples == 0)
                    return 0.5f;

                float avgVariance = totalVariance / samples;
                // Varianza baja = consistencia alta (máscara suave)
                // Normalizar: varianza < 1000 = buena consistencia
                float consistency = 1.0f - Math.Clamp(avgVariance / 1000.0f, 0.0f, 1.0f);
                return consistency;
            }
        }
    }
}






