# Ejemplo de Integración en KCMundialViewModel

## 1. Agregar campo privado

```csharp
private Services.BackgroundRemovalOrchestrator? _backgroundRemovalOrchestrator;
```

## 2. Inicializar en constructor (después de línea 93)

```csharp
_backgroundRemovalOrchestrator = new Services.BackgroundRemovalOrchestrator(
    _segmentationService,
    _backgroundSegmentationService,
    _alphaPostProcessor,
    _removeBgService,
    new Services.BackgroundRemovalOptions
    {
        OutputMaxSide = 1080,
        ConfidenceThreshold = 0.70f,
        EnableRemoteFallback = false  // Cambiar a true para habilitar Remove.bg fallback
    }
);
```

## 3. Reemplazar Pipeline A en CapturePhotoSequence (líneas 987-1053)

```csharp
// OPCIÓN: Usar orchestrator completo (recomendado)
if (_backgroundRemovalOrchestrator == null)
{
    throw new Exception("BackgroundRemovalOrchestrator no inicializado");
}

var removalResult = await _backgroundRemovalOrchestrator.RemoveBackgroundAsync(originalBitmap);
if (removalResult == null || !removalResult.IsValid)
{
    originalBitmap.Dispose();
    throw new Exception($"No se pudo remover fondo (confidence: {removalResult?.Confidence:F2})");
}

// Extraer cabeza y cuello del foreground resultante
var headAndNeckBitmap = ExtractHeadAndNeckFromOriginal(removalResult.ForegroundRgba!, removalResult.AlphaMask!);
if (headAndNeckBitmap == null || headAndNeckBitmap.IsNull)
{
    removalResult.Dispose();
    originalBitmap.Dispose();
    throw new Exception("No se pudo extraer cabeza y cuello");
}

// Guardar cabeza y cuello
var headAndNeckPath = Path.Combine(sessionPath, "photo_head_and_neck.png");
removalResult.SaveForegroundAsPng(headAndNeckPath);

// Log metadata
System.Diagnostics.Debug.WriteLine($"✓ Background removal: Confidence={removalResult.Confidence:F2}, Time={removalResult.ProcessingTimeMs}ms, Remote={removalResult.UsedRemoteFallback}");
Services.LogService.Write($"✓ Background removal: Confidence={removalResult.Confidence:F2}, Time={removalResult.ProcessingTimeMs}ms");

// Limpiar
removalResult.Dispose();
originalBitmap.Dispose();
```

## 4. (Opcional) Agregar comando Demo

```csharp
private Services.BackgroundRemovalDemo? _backgroundRemovalDemo;

// En constructor:
_backgroundRemovalDemo = new Services.BackgroundRemovalDemo(
    _backgroundRemovalOrchestrator!,
    new Services.BackgroundRemovalOptions { OutputMaxSide = 1080, ConfidenceThreshold = 0.70f }
);

// Método para ejecutar demo:
public async Task RunBackgroundRemovalDemoAsync()
{
    if (_backgroundRemovalDemo == null)
        return;

    try
    {
        int processed = await _backgroundRemovalDemo.ProcessSamplesAsync();
        System.Diagnostics.Debug.WriteLine($"✓ Demo completado: {processed} imágenes procesadas");
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"✗ Error en demo: {ex.Message}");
    }
}
```










