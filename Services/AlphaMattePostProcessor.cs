using System;
using System.Diagnostics;
using SkiaSharp;

namespace KCMundial.Services
{
    /// <summary>
    /// Postprocesa máscaras alpha para uniones suaves (erosion, blur, gamma, despill)
    /// </summary>
    public class AlphaMattePostProcessor
    {
        /// <summary>
        /// Postprocesa máscara alpha para preview (LIGERO: solo clamp core leve + blur pequeño)
        /// Desactivado: despill + blur selectivo pesado + erosión (solo para captura final)
        /// </summary>
        public SKBitmap? ProcessForPreview(SKBitmap alphaMask)
        {
            if (alphaMask == null || alphaMask.IsNull)
                return null;

            var sw = Stopwatch.StartNew();

            try
            {
                // Preview ligero: solo pasos esenciales para velocidad
                // 1. Clamp core leve: alpha>=240 -> 255, alpha<=10 -> 0 (más conservador que final)
                var clamped = ApplyClampCore(alphaMask, 10, 240);

                // 2. Blur gaussiano simple y rápido (radius pequeño: 0.5-1.0)
                // NO usar blur selectivo pesado - solo blur simple para suavizar bordes
                var blurred = ApplyGaussianBlur(clamped, 0.8f);

                // 3. NO aplicar erosión (muy costosa)
                // 4. NO aplicar despill (muy costoso)
                // Retornar directamente el blur

                // Limpiar temporales
                if (clamped != alphaMask) clamped?.Dispose();

                sw.Stop();
                Debug.WriteLine($"[AlphaMattePostProcessor] Preview ligero procesado en {sw.ElapsedMilliseconds}ms");

                return blurred;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error en ProcessForPreview: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Postprocesa máscara alpha para foto final (sin binarizar, solo refinar bordes)
        /// </summary>
        public SKBitmap? ProcessForFinal(SKBitmap alphaMask)
        {
            if (alphaMask == null || alphaMask.IsNull)
                return null;

            var sw = Stopwatch.StartNew();

            try
            {
                // NO aplicar threshold global - mantener alpha continuo
                // 1. Clamp core: alpha>=250 -> 255, alpha<=5 -> 0 (límites más conservadores)
                var clamped = ApplyClampCore(alphaMask, 5, 250);

                // 2. Blur selectivo: solo en banda intermedia (5..250) con radius conservador (final: 2-3)
                var blurred = ApplySelectiveBlur(clamped, 2.5f, 5, 250);

                // 3. Erosion mínima solo si se detecta halo (radius 1)
                var eroded = ApplyErosion(blurred, 1);

                // 4. Edge despill (retorna referencia si no modifica)
                var final = ApplyEdgeDespill(eroded, 0.20f);

                // Limpiar temporales (ApplyEdgeDespill puede retornar referencia, no copiar)
                if (clamped != alphaMask) clamped?.Dispose();
                if (blurred != clamped && blurred != alphaMask) blurred?.Dispose();
                if (eroded != blurred && eroded != alphaMask) eroded?.Dispose();
                // final puede ser la misma referencia que eroded si ApplyEdgeDespill no modifica
                if (final != eroded && final != clamped && final != blurred && final != alphaMask) final?.Dispose();

                sw.Stop();
                Debug.WriteLine($"[AlphaMattePostProcessor] Final procesado en {sw.ElapsedMilliseconds}ms");

                return final;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error en ProcessForFinal: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Aplica threshold a la máscara alpha
        /// </summary>
        private SKBitmap ApplyThreshold(SKBitmap mask, float threshold)
        {
            var result = new SKBitmap(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Opaque);
            var thresholdByte = (byte)(threshold * 255);

            unsafe
            {
                var sourcePtr = (byte*)mask.GetPixels();
                var destPtr = (byte*)result.GetPixels();
                var stride = mask.RowBytes;

                for (int y = 0; y < mask.Height; y++)
                {
                    for (int x = 0; x < mask.Width; x++)
                    {
                        byte alpha = sourcePtr[y * stride + x];
                        destPtr[y * stride + x] = (byte)(alpha >= thresholdByte ? 255 : 0);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Aplica erosión morfológica (reduce halo)
        /// </summary>
        private SKBitmap ApplyErosion(SKBitmap mask, int radius)
        {
            var result = new SKBitmap(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Opaque);

            unsafe
            {
                var sourcePtr = (byte*)mask.GetPixels();
                var destPtr = (byte*)result.GetPixels();
                var stride = mask.RowBytes;

                for (int y = 0; y < mask.Height; y++)
                {
                    for (int x = 0; x < mask.Width; x++)
                    {
                        byte minAlpha = 255;

                        // Buscar mínimo en vecindario
                        for (int dy = -radius; dy <= radius; dy++)
                        {
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                int nx = x + dx;
                                int ny = y + dy;

                                if (nx >= 0 && nx < mask.Width && ny >= 0 && ny < mask.Height)
                                {
                                    byte alpha = sourcePtr[ny * stride + nx];
                                    if (alpha < minAlpha)
                                        minAlpha = alpha;
                                }
                            }
                        }

                        destPtr[y * stride + x] = minAlpha;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Aplica blur gaussiano a la máscara usando SKImageFilter
        /// </summary>
        private SKBitmap ApplyGaussianBlur(SKBitmap mask, float radius)
        {
            var result = new SKBitmap(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Opaque);
            
            using (var image = SKImage.FromBitmap(mask))
            {
                using (var filter = SKImageFilter.CreateBlur(radius, radius))
                {
                    using (var paint = new SKPaint { ImageFilter = filter })
                    {
                        using (var surface = SKSurface.Create(new SKImageInfo(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Opaque)))
                        {
                            var canvas = surface.Canvas;
                            canvas.Clear(SKColors.Transparent);
                            canvas.DrawImage(image, 0, 0, paint);
                            
                            using (var snapshot = surface.Snapshot())
                            {
                                snapshot.ReadPixels(new SKImageInfo(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Opaque), result.GetPixels(), result.RowBytes, 0, 0);
                            }
                        }
                    }
                }
            }
            
            return result;
        }

        /// <summary>
        /// Aplica corrección gamma a la máscara (mantiene interior sólido)
        /// </summary>
        private SKBitmap ApplyGamma(SKBitmap mask, float gamma)
        {
            var result = new SKBitmap(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Opaque);

            unsafe
            {
                var sourcePtr = (byte*)mask.GetPixels();
                var destPtr = (byte*)result.GetPixels();
                var stride = mask.RowBytes;

                for (int y = 0; y < mask.Height; y++)
                {
                    for (int x = 0; x < mask.Width; x++)
                    {
                        float alpha = sourcePtr[y * stride + x] / 255.0f;
                        alpha = (float)Math.Pow(alpha, gamma);
                        destPtr[y * stride + x] = (byte)(alpha * 255);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Clamp core: alpha>=high -> 255, alpha<=low -> 0, mantiene continuidad
        /// </summary>
        private SKBitmap ApplyClampCore(SKBitmap mask, byte low, byte high)
        {
            var result = new SKBitmap(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Opaque);

            unsafe
            {
                var sourcePtr = (byte*)mask.GetPixels();
                var destPtr = (byte*)result.GetPixels();
                var stride = mask.RowBytes;

                for (int y = 0; y < mask.Height; y++)
                {
                    for (int x = 0; x < mask.Width; x++)
                    {
                        byte alpha = sourcePtr[y * stride + x];
                        if (alpha >= high)
                            destPtr[y * stride + x] = 255;
                        else if (alpha <= low)
                            destPtr[y * stride + x] = 0;
                        else
                            destPtr[y * stride + x] = alpha; // Mantener continuidad
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Blur selectivo: solo en banda intermedia (low..high) para suavizar borde sin destruir núcleo
        /// </summary>
        private SKBitmap ApplySelectiveBlur(SKBitmap mask, float radius, byte low, byte high)
        {
            // Primero aplicar blur completo
            var blurred = ApplyGaussianBlur(mask, radius);
            
            // Luego combinar: usar blur solo en banda intermedia, mantener núcleo y fondo
            var result = new SKBitmap(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Opaque);

            unsafe
            {
                var sourcePtr = (byte*)mask.GetPixels();
                var blurredPtr = (byte*)blurred.GetPixels();
                var destPtr = (byte*)result.GetPixels();
                var stride = mask.RowBytes;

                for (int y = 0; y < mask.Height; y++)
                {
                    for (int x = 0; x < mask.Width; x++)
                    {
                        byte origAlpha = sourcePtr[y * stride + x];
                        byte blurAlpha = blurredPtr[y * stride + x];
                        
                        if (origAlpha >= high || origAlpha <= low)
                        {
                            // Núcleo o fondo: mantener original
                            destPtr[y * stride + x] = origAlpha;
                        }
                        else
                        {
                            // Banda intermedia: usar blur
                            destPtr[y * stride + x] = blurAlpha;
                        }
                    }
                }
            }

            blurred.Dispose();
            return result;
        }

        /// <summary>
        /// Aplica edge despill (reducción de saturación en borde para reducir halo)
        /// Nota: Esta función modifica la máscara pero el despill real se aplica en BackgroundComposer
        /// Ownership: El caller es responsable de dispose del resultado.
        /// Si no modifica pixels, retorna la misma referencia (no copia).
        /// </summary>
        private SKBitmap ApplyEdgeDespill(SKBitmap mask, float strength)
        {
            // Por ahora no modifica pixels - el despill real se aplica en BackgroundComposer al componer
            // Retornar referencia (no copia) ya que no hay cambios
            return mask;
        }
    }
}
