# Background Removal - Remove.bg-like Quality

## Arquitectura

**Opción elegida: C (Híbrido Local + Fallback Remoto)**

- **Local**: DeepLabV3 (coarse mask) + post-procesamiento avanzado
- **Fallback**: Remove.bg API si confidence < threshold
- **Confidence**: Edge quality (40%) + Coverage (35%) + Consistency (25%)

## Archivos Creados

```
Services/
├── BackgroundRemovalOptions.cs          # Configuración
├── BackgroundRemovalResult.cs          # Resultado con metadata
├── ConfidenceCalculator.cs              # Cálculo de confidence
├── BackgroundRemovalOrchestrator.cs   # Orquestador principal
└── BackgroundRemovalDemo.cs           # Demo: procesa /samples/in -> /samples/out
```

## NuGet Packages

**No se requieren packages adicionales.** Usa los existentes:
- SkiaSharp 2.88.6
- Microsoft.ML.OnnxRuntime 1.19.0
- System.Net.Http 4.3.4 (ya incluido)

## Modelos ONNX

**Modelo actual**: DeepLabV3 (513x513)
- Ubicación: `Assets/models/deeplabv3.onnx` o `Assets/models/person_segmentation.onnx`
- Si no existe, usa segmentación básica (fallback)

**No se requiere modelo adicional** para el pipeline híbrido.

## GPU Provider (Opcional)

Para habilitar GPU en ONNX Runtime:

```csharp
var options = new SessionOptions();
options.AppendExecutionProvider_Cuda(); // NVIDIA GPU
// o
options.AppendExecutionProvider_Dml();  // DirectML (Windows GPU)
```

Actualmente usa CPU por defecto. Para habilitar GPU, modificar `PersonSegmentationService.Initialize()`.

## Uso Básico

### 1. Inicializar Orchestrator

```csharp
var options = new BackgroundRemovalOptions
{
    OutputMaxSide = 1080,
    ConfidenceThreshold = 0.70f,
    EnableRemoteFallback = true  // Habilitar Remove.bg si confidence bajo
};

var orchestrator = new BackgroundRemovalOrchestrator(
    _segmentationService,
    _backgroundSegmentationService,
    _alphaPostProcessor,
    _removeBgService,  // Opcional
    options
);
```

### 2. Procesar Imagen

```csharp
using (var inputBitmap = SKBitmap.Decode("input.jpg"))
{
    var result = await orchestrator.RemoveBackgroundAsync(inputBitmap);
    
    if (result.IsValid)
    {
        // Guardar foreground
        result.SaveForegroundAsPng("output.png");
        
        // Acceder a metadata
        Debug.WriteLine($"Confidence: {result.Confidence:F2}");
        Debug.WriteLine($"Time: {result.ProcessingTimeMs}ms");
        Debug.WriteLine($"Remote: {result.UsedRemoteFallback}");
    }
    
    result.Dispose();
}
```

### 3. Componer sobre Background

```csharp
var composed = orchestrator.ComposeForegroundOverBackground(
    result.ForegroundRgba,
    result.AlphaMask,
    backgroundBitmap
);
```

## Integración en ViewModel

### En Constructor

```csharp
// Crear orchestrator
_backgroundRemovalOrchestrator = new BackgroundRemovalOrchestrator(
    _segmentationService,
    _backgroundSegmentationService,
    _alphaPostProcessor,
    _removeBgService,
    new BackgroundRemovalOptions
    {
        OutputMaxSide = 1080,
        ConfidenceThreshold = 0.70f,
        EnableRemoteFallback = false  // Cambiar a true para habilitar fallback
    }
);
```

### En CapturePhotoSequence (Pipeline A)

```csharp
// Reemplazar el pipeline actual con:
using (var originalBitmap = SKBitmap.Decode(originalPhotoPath))
{
    var result = await _backgroundRemovalOrchestrator.RemoveBackgroundAsync(originalBitmap);
    
    if (result.IsValid && result.ForegroundRgba != null)
    {
        // Usar result.ForegroundRgba en lugar de headAndNeckBitmap
        var headAndNeckPath = Path.Combine(sessionPath, "photo_head_and_neck.png");
        result.SaveForegroundAsPng(headAndNeckPath);
        
        // Log metadata
        Services.LogService.Write($"Confidence: {result.Confidence:F2}, Time: {result.ProcessingTimeMs}ms");
    }
    
    result.Dispose();
}
```

## Demo: Procesar Samples

```csharp
var demo = new BackgroundRemovalDemo(_backgroundRemovalOrchestrator);
int processed = await demo.ProcessSamplesAsync();
```

**Estructura esperada:**
```
Desktop/KCMundial/
├── samples/
│   ├── in/          # Imágenes de entrada (.jpg, .png, .bmp)
│   └── out/         # Resultados generados
│       ├── {name}_foreground.png
│       ├── {name}_mask.png
│       └── {name}_metadata.txt
```

## Configuración

### BackgroundRemovalOptions

```csharp
var options = new BackgroundRemovalOptions
{
    PreviewMaxSide = 320,              // Tamaño para preview
    OutputMaxSide = 1080,              // Tamaño para output final
    UseGpu = true,                     // GPU si disponible (requiere modificar PersonSegmentationService)
    ConfidenceThreshold = 0.70f,        // Threshold para considerar válido
    EnableRemoteFallback = false,      // Habilitar Remove.bg fallback
    FeatherPx = 2.0f,                   // Radio de feather
    DehaloStrength = 0.15f,            // Fuerza de dehalo
    ErosionRadius = 1,                 // Radio de erosión
    BlurRadius = 1.5f,                 // Radio de blur
    Gamma = 1.2f,                      // Gamma correction
    Threshold = 0.40f                   // Threshold inicial
};
```

## Confidence Calculation

El confidence se calcula como:
- **Edge Quality (40%)**: Gradiente de bordes (bordes definidos = alta calidad)
- **Coverage (35%)**: Área de foreground vs total (persona presente)
- **Consistency (25%)**: Variación espacial (máscara coherente)

**Rango**: 0.0 (inválido) a 1.0 (perfecto)

## Performance Objetivo

- **Preview (320px)**: ≤ 300ms
- **Final (720-1080px)**: ≤ 1.5s (CPU), mejor con GPU

## Acceptance Criteria

✅ Bordes sin serrucho y sin halo blanco  
✅ No comerse orejas/hombros  
✅ Cabello aceptable (no perfecto, pero sin "casco")  
✅ Outputs en `/samples/out` desde `/samples/in`  
✅ Confidence < threshold → marcado y (si enabled) fallback remoto

## Troubleshooting

**Confidence bajo (< 0.70)**
- Verificar que el modelo ONNX existe y está cargado
- Ajustar `ConfidenceThreshold` si es necesario
- Habilitar `EnableRemoteFallback` para usar Remove.bg

**Performance lenta**
- Reducir `OutputMaxSide` (ej: 720 en lugar de 1080)
- Habilitar GPU si está disponible
- Reducir `BlurRadius` y `FeatherPx`

**Remove.bg no funciona**
- Verificar que `removebg_api_key.txt` existe en `Desktop/KCMundial/`
- Verificar conexión a internet
- Verificar créditos de API










