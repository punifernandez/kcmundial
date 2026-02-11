using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SkiaSharp;

namespace KCMundial.Services
{
    /// <summary>
    /// Demo: procesa imágenes de /samples/in y exporta a /samples/out
    /// </summary>
    public class BackgroundRemovalDemo
    {
        private readonly BackgroundRemovalOrchestrator _orchestrator;
        private readonly BackgroundRemovalOptions _options;

        public BackgroundRemovalDemo(BackgroundRemovalOrchestrator orchestrator, BackgroundRemovalOptions? options = null)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _options = options ?? new BackgroundRemovalOptions();
        }

        /// <summary>
        /// Procesa todas las imágenes de /samples/in y guarda resultados en /samples/out
        /// </summary>
        public async Task<int> ProcessSamplesAsync(string? inputDir = null, string? outputDir = null)
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            inputDir ??= Path.Combine(desktop, "KCMundial", "samples", "in");
            outputDir ??= Path.Combine(desktop, "KCMundial", "samples", "out");

            if (!Directory.Exists(inputDir))
            {
                Debug.WriteLine($"✗ Directorio de entrada no existe: {inputDir}");
                return 0;
            }

            Directory.CreateDirectory(outputDir);

            var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp" };
            var imageFiles = Directory.GetFiles(inputDir)
                .Where(f => imageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();

            if (imageFiles.Count == 0)
            {
                Debug.WriteLine($"⚠ No se encontraron imágenes en: {inputDir}");
                return 0;
            }

            Debug.WriteLine($"=== Procesando {imageFiles.Count} imágenes ===");

            int successCount = 0;
            int lowConfidenceCount = 0;

            foreach (var imagePath in imageFiles)
            {
                try
                {
                    Debug.WriteLine($"\nProcesando: {Path.GetFileName(imagePath)}");

                    // Cargar imagen
                    SKBitmap? inputBitmap = null;
                    using (var stream = File.OpenRead(imagePath))
                    {
                        inputBitmap = SKBitmap.Decode(stream);
                    }

                    if (inputBitmap == null || inputBitmap.IsNull)
                    {
                        Debug.WriteLine($"  ✗ No se pudo cargar imagen");
                        continue;
                    }

                    // Procesar
                    var result = await _orchestrator.RemoveBackgroundAsync(inputBitmap);
                    inputBitmap.Dispose();

                    if (result == null || !result.IsValid)
                    {
                        Debug.WriteLine($"  ✗ Resultado inválido (confidence: {result?.Confidence:F2})");
                        result?.Dispose();
                        continue;
                    }

                    // Guardar resultados
                    string baseName = Path.GetFileNameWithoutExtension(imagePath);
                    string foregroundPath = Path.Combine(outputDir, $"{baseName}_cutout.png");
                    string alphaPath = Path.Combine(outputDir, $"{baseName}_alpha.png");
                    string metadataPath = Path.Combine(outputDir, $"{baseName}_metadata.txt");
                    string reportPath = Path.Combine(outputDir, $"{baseName}_report.json");

                    // Guardar cutout (foreground con alpha)
                    result.SaveForegroundAsPng(foregroundPath);
                    
                    // Guardar máscara alpha continua
                    result.SaveAlphaMaskAsPng(alphaPath);

                    // Calcular estadísticas de alpha
                    var alphaStats = result.CalculateAlphaStatistics();

                    // Guardar report.json con estadísticas y timings
                    var report = new
                    {
                        Input = Path.GetFileName(imagePath),
                        Cutout = Path.GetFileName(foregroundPath),
                        AlphaMask = Path.GetFileName(alphaPath),
                        Confidence = result.Confidence,
                        Threshold = _options.ConfidenceThreshold,
                        UsedRemoteFallback = result.UsedRemoteFallback,
                        Timings = new
                        {
                            Total = result.ProcessingTimeMs,
                            Segmentation = result.SegmentationTimeMs,
                            PostProcessing = result.PostProcessingTimeMs
                        },
                        AlphaStatistics = new
                        {
                            Min = alphaStats.Min,
                            Max = alphaStats.Max,
                            Mean = Math.Round(alphaStats.Mean, 2),
                            PercentLow = Math.Round(alphaStats.PercentLow, 2),   // (0, 5]
                            PercentMid = Math.Round(alphaStats.PercentMid, 2),    // (5, 250)
                            PercentHigh = Math.Round(alphaStats.PercentHigh, 2)    // [250, 255]
                        },
                        Status = result.Confidence >= _options.ConfidenceThreshold ? "VALID" : "LOW_CONFIDENCE"
                    };

                    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, jsonOptions));

                    // Guardar metadata con timings (legacy, texto)
                    File.WriteAllText(metadataPath, $@"Background Removal Metadata
===============================
Input: {Path.GetFileName(imagePath)}
Cutout: {Path.GetFileName(foregroundPath)}
Alpha Mask: {Path.GetFileName(alphaPath)}
Report: {Path.GetFileName(reportPath)}
Confidence: {result.Confidence:F2}
Threshold: {_options.ConfidenceThreshold:F2}
Used Remote Fallback: {result.UsedRemoteFallback}
Processing Time: {result.ProcessingTimeMs}ms
  - Segmentation: {result.SegmentationTimeMs}ms
  - Post-processing: {result.PostProcessingTimeMs}ms
Alpha Statistics:
  - Min: {alphaStats.Min}, Max: {alphaStats.Max}, Mean: {alphaStats.Mean:F2}
  - Low (0-5): {alphaStats.PercentLow:F2}%
  - Mid (5-250): {alphaStats.PercentMid:F2}%
  - High (250-255): {alphaStats.PercentHigh:F2}%
Status: {(result.Confidence >= _options.ConfidenceThreshold ? "✓ VALID" : "⚠ LOW CONFIDENCE")}
");

                    Debug.WriteLine($"  ✓ Guardado: {foregroundPath}");
                    Debug.WriteLine($"    Confidence: {result.Confidence:F2}, Time: {result.ProcessingTimeMs}ms, Remote: {result.UsedRemoteFallback}");

                    if (result.Confidence < _options.ConfidenceThreshold)
                        lowConfidenceCount++;

                    successCount++;
                    result.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"  ✗ Error: {ex.Message}");
                }
            }

            Debug.WriteLine($"\n=== Resumen ===");
            Debug.WriteLine($"Procesadas: {successCount}/{imageFiles.Count}");
            Debug.WriteLine($"Low confidence: {lowConfidenceCount}");
            Debug.WriteLine($"Resultados en: {outputDir}");

            return successCount;
        }
    }
}

