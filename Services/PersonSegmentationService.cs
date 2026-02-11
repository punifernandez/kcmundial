using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace KCMundial.Services
{
    /// <summary>
    /// Servicio para detectar y segmentar personas en imágenes, eliminando el fondo
    /// </summary>
    public class PersonSegmentationService : IDisposable
    {
        private InferenceSession? _session;
        private bool _isInitialized = false;
        private const int MODEL_INPUT_SIZE = 513; // Tamaño de entrada del modelo DeepLabV3
        private bool _enableLegacyFallback = false; // NO usar RemoveBackgroundBasic como fallback de calidad por defecto

        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Inicializa el servicio con un modelo ONNX de segmentación
        /// </summary>
        public bool Initialize(string? modelPath = null)
        {
            try
            {
                // Buscar el modelo en múltiples ubicaciones
                if (string.IsNullOrEmpty(modelPath))
                {
                    var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    var possiblePaths = new[]
                    {
                        Path.Combine(desktop, "KCMundial", "Assets", "models", "deeplabv3.onnx"),
                        Path.Combine(desktop, "KCMundial", "Assets", "models", "person_segmentation.onnx"),
                        Path.Combine(AppContext.BaseDirectory, "Assets", "models", "deeplabv3.onnx"),
                        Path.Combine(AppContext.BaseDirectory, "Assets", "models", "person_segmentation.onnx")
                    };

                    foreach (var path in possiblePaths)
                    {
                        if (File.Exists(path))
                        {
                            modelPath = path;
                            System.Diagnostics.Debug.WriteLine($"✓ Modelo encontrado: {path}");
                            break;
                        }
                    }
                }

                // Si no hay modelo, usar segmentación básica con detección de bordes
                if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
                {
                    System.Diagnostics.Debug.WriteLine("⚠ No se encontró modelo ONNX, usando segmentación básica");
                    _isInitialized = true; // Inicializado pero sin modelo ML
                    return true;
                }

                // Cargar modelo ONNX
                var options = new SessionOptions();
                options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                _session = new InferenceSession(modelPath, options);
                _isInitialized = true;
                System.Diagnostics.Debug.WriteLine($"✓ Modelo ONNX cargado exitosamente");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al inicializar PersonSegmentationService: {ex.Message}");
                _isInitialized = false;
                return false;
            }
        }

        /// <summary>
        /// Elimina el fondo de una imagen, dejando solo la persona
        /// </summary>
        public SKBitmap? RemoveBackground(SKBitmap originalBitmap)
        {
            if (originalBitmap == null || originalBitmap.IsNull)
                return null;

            try
            {
                // Si hay modelo ONNX, usarlo
                if (_session != null && _isInitialized)
                {
                    return RemoveBackgroundWithModel(originalBitmap);
                }
                else
                {
                    // Usar método básico de segmentación (detección de bordes + flood fill)
                    return RemoveBackgroundBasic(originalBitmap);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al eliminar fondo: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extrae solo la cabeza de una imagen con fondo removido
        /// </summary>
        public SKBitmap? ExtractHeadOnly(SKBitmap personBitmap)
        {
            if (personBitmap == null || personBitmap.IsNull)
                return null;

            try
            {
                // Encontrar los límites de la persona (área no transparente)
                int minX = personBitmap.Width, minY = personBitmap.Height;
                int maxX = 0, maxY = 0;
                bool foundPerson = false;

                unsafe
                {
                    var ptr = (uint*)personBitmap.GetPixels();
                    var stride = personBitmap.RowBytes / 4;

                    for (int y = 0; y < personBitmap.Height; y++)
                    {
                        for (int x = 0; x < personBitmap.Width; x++)
                        {
                            var pixel = ptr[y * stride + x];
                            var color = new SKColor((uint)pixel);
                            
                            // Si el píxel no es transparente, es parte de la persona
                            if (color.Alpha > 0)
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
                    System.Diagnostics.Debug.WriteLine("No se encontró persona en la imagen");
                    return null;
                }

                int personWidth = maxX - minX + 1;
                int personHeight = maxY - minY + 1;

                // La cabeza es aproximadamente el 35% superior de la persona
                // Ajustar para incluir un poco más hacia abajo (hasta el cuello)
                int headHeight = (int)(personHeight * 0.40); // 40% desde arriba
                int headY = minY;
                int headX = minX;
                int headWidth = personWidth;

                // Asegurar que no exceda los límites
                if (headY + headHeight > personBitmap.Height)
                    headHeight = personBitmap.Height - headY;
                if (headX + headWidth > personBitmap.Width)
                    headWidth = personBitmap.Width - headX;

                // Crear bitmap solo con la cabeza
                var headBitmap = new SKBitmap(headWidth, headHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
                
                unsafe
                {
                    var sourcePtr = (uint*)personBitmap.GetPixels();
                    var destPtr = (uint*)headBitmap.GetPixels();
                    var sourceStride = personBitmap.RowBytes / 4;
                    var destStride = headBitmap.RowBytes / 4;

                    for (int y = 0; y < headHeight; y++)
                    {
                        for (int x = 0; x < headWidth; x++)
                        {
                            int sourceX = headX + x;
                            int sourceY = headY + y;
                            
                            if (sourceX < personBitmap.Width && sourceY < personBitmap.Height)
                            {
                                destPtr[y * destStride + x] = sourcePtr[sourceY * sourceStride + sourceX];
                            }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"✓ Cabeza extraída: {headWidth}x{headHeight} desde ({headX}, {headY})");
                return headBitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al extraer cabeza: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Obtiene máscara alpha directamente desde el modelo (sin aplicar a imagen)
        /// </summary>
        public SKBitmap? GetAlphaMatte(SKBitmap originalBitmap)
        {
            if (_session == null || !_isInitialized)
                return null;

            try
            {
                var inputTensor = PreprocessImage(originalBitmap);
                if (inputTensor == null)
                    return null;

                var inputName = _session.InputMetadata.Keys.First();
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
                };

                using (var results = _session.Run(inputs))
                {
                    var output = results.First().Value as Tensor<float>;
                    if (output == null)
                        return null;

                    // Postprocesar para obtener máscara alpha continua
                    return PostprocessMask(output, originalBitmap.Width, originalBitmap.Height);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error en GetAlphaMatte: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Elimina el fondo usando modelo ONNX (más preciso)
        /// </summary>
        private SKBitmap? RemoveBackgroundWithModel(SKBitmap originalBitmap)
        {
            try
            {
                // Preprocesar imagen para el modelo
                var inputTensor = PreprocessImage(originalBitmap);
                if (inputTensor == null)
                {
                    if (_enableLegacyFallback)
                        return RemoveBackgroundBasic(originalBitmap);
                    return null; // Sin fallback, devolver null
                }

                // Ejecutar inferencia - el nombre del input puede variar según el modelo
                var inputName = _session!.InputMetadata.Keys.First();
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
                };

                using (var results = _session.Run(inputs))
                {
                    var output = results.First().Value as Tensor<float>;
                    if (output == null)
                    {
                        if (_enableLegacyFallback)
                            return RemoveBackgroundBasic(originalBitmap);
                        return null;
                    }

                    // Postprocesar para obtener la máscara
                    var mask = PostprocessMask(output, originalBitmap.Width, originalBitmap.Height);
                    
                    // Aplicar máscara a la imagen original
                    var result = ApplyMask(originalBitmap, mask);
                    mask.Dispose();
                    return result;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error en RemoveBackgroundWithModel: {ex.Message}");
                if (_enableLegacyFallback)
                    return RemoveBackgroundBasic(originalBitmap);
                return null; // Sin fallback, devolver null
            }
        }

        /// <summary>
        /// Método básico mejorado de eliminación de fondo usando análisis de color, posición y bordes
        /// </summary>
        private SKBitmap RemoveBackgroundBasic(SKBitmap originalBitmap)
        {
            try
            {
                var width = originalBitmap.Width;
                var height = originalBitmap.Height;
                var resultBitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);

                // Obtener muestra de colores del centro (área donde probablemente está la persona)
                var centerX = width / 2;
                var centerY = height / 2;
                var sampleSize = Math.Min(width, height) / 4;
                
                // Calcular color promedio del área central (más confiable que un solo píxel)
                long totalR = 0, totalG = 0, totalB = 0;
                int sampleCount = 0;
                
                for (int y = centerY - sampleSize / 2; y < centerY + sampleSize / 2 && y < height; y++)
                {
                    if (y < 0) continue;
                    for (int x = centerX - sampleSize / 2; x < centerX + sampleSize / 2 && x < width; x++)
                    {
                        if (x < 0) continue;
                        var color = originalBitmap.GetPixel(x, y);
                        totalR += color.Red;
                        totalG += color.Green;
                        totalB += color.Blue;
                        sampleCount++;
                    }
                }
                
                if (sampleCount == 0)
                {
                    // Fallback: usar imagen original sin procesar
                    originalBitmap.CopyTo(resultBitmap);
                    return resultBitmap;
                }
                
                var avgR = (int)(totalR / sampleCount);
                var avgG = (int)(totalG / sampleCount);
                var avgB = (int)(totalB / sampleCount);
                
                // Obtener color promedio de los bordes (probablemente fondo)
                long borderR = 0, borderG = 0, borderB = 0;
                int borderCount = 0;
                var borderMargin = Math.Min(width, height) / 10;
                
                // Muestrear bordes superior, inferior, izquierdo y derecho
                for (int i = 0; i < borderMargin; i++)
                {
                    // Borde superior
                    if (i < width)
                    {
                        var color = originalBitmap.GetPixel(i, 0);
                        borderR += color.Red; borderG += color.Green; borderB += color.Blue; borderCount++;
                    }
                    // Borde inferior
                    if (i < width)
                    {
                        var color = originalBitmap.GetPixel(i, height - 1);
                        borderR += color.Red; borderG += color.Green; borderB += color.Blue; borderCount++;
                    }
                    // Borde izquierdo
                    if (i < height)
                    {
                        var color = originalBitmap.GetPixel(0, i);
                        borderR += color.Red; borderG += color.Green; borderB += color.Blue; borderCount++;
                    }
                    // Borde derecho
                    if (i < height)
                    {
                        var color = originalBitmap.GetPixel(width - 1, i);
                        borderR += color.Red; borderG += color.Green; borderB += color.Blue; borderCount++;
                    }
                }
                
                var avgBorderR = borderCount > 0 ? (int)(borderR / borderCount) : avgR;
                var avgBorderG = borderCount > 0 ? (int)(borderG / borderCount) : avgG;
                var avgBorderB = borderCount > 0 ? (int)(borderB / borderCount) : avgB;
                
                // Crear máscara mejorada con suavizado de bordes
                // Primero crear una máscara binaria
                var mask = new bool[width * height];
                
                unsafe
                {
                    var originalPtr = (uint*)originalBitmap.GetPixels();
                    var stride = originalBitmap.RowBytes / 4;

                    // Paso 1: Crear máscara inicial más agresiva
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            var pixel = originalPtr[y * stride + x];
                            var color = new SKColor((uint)pixel);
                            
                            // Calcular distancia al color promedio del centro (persona)
                            var distToCenter = Math.Sqrt(
                                Math.Pow(color.Red - avgR, 2) +
                                Math.Pow(color.Green - avgG, 2) +
                                Math.Pow(color.Blue - avgB, 2)
                            );
                            
                            // Calcular distancia al color promedio del borde (fondo)
                            var distToBorder = Math.Sqrt(
                                Math.Pow(color.Red - avgBorderR, 2) +
                                Math.Pow(color.Green - avgBorderG, 2) +
                                Math.Pow(color.Blue - avgBorderB, 2)
                            );
                            
                            // Calcular distancia al centro geométrico
                            var centerDist = Math.Sqrt(
                                Math.Pow(x - centerX, 2) + Math.Pow(y - centerY, 2)
                            );
                            var maxDist = Math.Sqrt(centerX * centerX + centerY * centerY);
                            var normalizedDist = centerDist / maxDist;
                            
                            // Decisión más agresiva: eliminar más fondo
                            // Aumentar el umbral de distancia al centro para ser más restrictivo
                            bool isPerson;
                            
                            if (normalizedDist < 0.30) // Reducido de 0.35 a 0.30 para ser más restrictivo
                            {
                                // Zona central - muy probable que sea persona
                                isPerson = distToCenter < distToBorder + 20;
                            }
                            else if (normalizedDist > 0.75)
                            {
                                // Zona de borde - muy probable que sea fondo (más agresivo)
                                isPerson = distToBorder > distToCenter + 40 && distToCenter < 100;
                            }
                            else
                            {
                                // Zona intermedia - comparar distancias con umbral más estricto
                                // Ser más restrictivo: solo si está claramente más cerca del color de la persona
                                isPerson = distToCenter < distToBorder - 20; // Aumentado de -10 a -20 para ser más restrictivo
                            }
                            
                            // Verificar si está en los bordes absolutos de la imagen (siempre fondo)
                            if (x < width * 0.05 || x > width * 0.95 || y < height * 0.05 || y > height * 0.95)
                            {
                                // En los bordes absolutos, ser más estricto
                                isPerson = isPerson && distToCenter < 60; // Reducido de 80 a 60 para ser más restrictivo
                            }
                            
                            mask[y * width + x] = isPerson;
                        }
                    }
                    
                    // Paso 2: Aplicar filtro de mediana para suavizar bordes y eliminar ruido
                    var smoothedMask = new bool[width * height];
                    Array.Copy(mask, smoothedMask, mask.Length);
                    
                    int kernelSize = 3;
                    int halfKernel = kernelSize / 2;
                    
                    for (int y = halfKernel; y < height - halfKernel; y++)
                    {
                        for (int x = halfKernel; x < width - halfKernel; x++)
                        {
                            int personCount = 0;
                            int totalCount = 0;
                            
                            for (int ky = -halfKernel; ky <= halfKernel; ky++)
                            {
                                for (int kx = -halfKernel; kx <= halfKernel; kx++)
                                {
                                    int idx = (y + ky) * width + (x + kx);
                                    if (mask[idx]) personCount++;
                                    totalCount++;
                                }
                            }
                            
                            // Si la mayoría del vecindario es persona, mantener como persona
                            smoothedMask[y * width + x] = personCount > totalCount / 2;
                        }
                    }
                    
                    // Paso 3: Aplicar máscara suavizada con feathering en los bordes
                    var resultPtr = (uint*)resultBitmap.GetPixels();
                    
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int idx = y * width + x;
                            var pixel = originalPtr[y * stride + x];
                            
                            if (smoothedMask[idx])
                            {
                                // Calcular distancia al borde más cercano para feathering
                                int distToEdge = Math.Min(Math.Min(x, width - 1 - x), Math.Min(y, height - 1 - y));
                                int featherRadius = 5; // Radio de suavizado
                                
                                if (distToEdge < featherRadius)
                                {
                                    // Aplicar feathering: reducir alpha gradualmente cerca de los bordes
                                    float alpha = Math.Min(1.0f, distToEdge / (float)featherRadius);
                                    var color = new SKColor((uint)pixel);
                                    var featheredColor = new SKColor(color.Red, color.Green, color.Blue, (byte)(color.Alpha * alpha));
                                    resultPtr[y * stride + x] = (uint)featheredColor;
                                }
                                else
                                {
                                    // Mantener píxel original intacto
                                    resultPtr[y * stride + x] = pixel;
                                }
                            }
                            else
                            {
                                // Fondo eliminado - completamente transparente
                                resultPtr[y * stride + x] = 0;
                            }
                        }
                    }
                }

                return resultBitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error en RemoveBackgroundBasic: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"StackTrace: {ex.StackTrace}");
                // Si falla, devolver imagen original sin procesar (sin eliminar fondo)
                var fallback = new SKBitmap(originalBitmap.Width, originalBitmap.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                originalBitmap.CopyTo(fallback);
                return fallback;
            }
        }

        /// <summary>
        /// Preprocesa la imagen para el modelo ONNX
        /// </summary>
        private Tensor<float>? PreprocessImage(SKBitmap bitmap)
        {
            try
            {
                // Redimensionar a tamaño del modelo
                var resized = bitmap.Resize(new SKImageInfo(MODEL_INPUT_SIZE, MODEL_INPUT_SIZE), SKFilterQuality.High);
                if (resized == null || resized.IsNull)
                    return null;

                // Convertir a tensor [1, 3, 513, 513] normalizado
                var tensor = new DenseTensor<float>(new[] { 1, 3, MODEL_INPUT_SIZE, MODEL_INPUT_SIZE });
                
                unsafe
                {
                    var ptr = (uint*)resized.GetPixels();
                    var stride = resized.RowBytes / 4;

                    for (int y = 0; y < MODEL_INPUT_SIZE; y++)
                    {
                        for (int x = 0; x < MODEL_INPUT_SIZE; x++)
                        {
                            var pixel = ptr[y * stride + x];
                            var color = new SKColor((uint)pixel);
                            
                            // Normalizar a [-1, 1] (normalización ImageNet)
                            tensor[0, 0, y, x] = (color.Red / 255.0f - 0.485f) / 0.229f;
                            tensor[0, 1, y, x] = (color.Green / 255.0f - 0.456f) / 0.224f;
                            tensor[0, 2, y, x] = (color.Blue / 255.0f - 0.406f) / 0.225f;
                        }
                    }
                }

                resized.Dispose();
                return tensor;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error en PreprocessImage: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Postprocesa la máscara del modelo con interpolación bilineal para alpha continuo (0-255)
        /// </summary>
        private SKBitmap PostprocessMask(Tensor<float> maskTensor, int originalWidth, int originalHeight)
        {
            // Crear máscara Alpha8 continua (NO Gray8, NO binarizada)
            var mask = new SKBitmap(originalWidth, originalHeight, SKColorType.Alpha8, SKAlphaType.Opaque);
            
            // Asumir que el tensor es [batch, channels, height, width] o [height, width]
            // DeepLabV3 típicamente devuelve [1, 21, 513, 513] o [513, 513]
            // Necesitamos extraer la clase "person" (índice 15 en Pascal VOC)
            var tensorDims = maskTensor.Dimensions.ToArray();
            int maskWidth, maskHeight;
            int tensorOffset = 0;

            try
            {
                if (tensorDims.Length == 4)
                {
                    // [batch, channels, height, width] - DeepLabV3 formato
                    if (tensorDims[2] <= 0 || tensorDims[3] <= 0)
                        throw new ArgumentException("Invalid tensor dimensions");
                    maskHeight = tensorDims[2];
                    maskWidth = tensorDims[3];
                    // Clase 15 = person, offset = batch*channels*height*width + 15*height*width
                    tensorOffset = 15 * maskHeight * maskWidth;
                }
                else if (tensorDims.Length == 2)
                {
                    // [height, width] - ya procesado
                    if (tensorDims[0] <= 0 || tensorDims[1] <= 0)
                        throw new ArgumentException("Invalid tensor dimensions");
                    maskHeight = tensorDims[0];
                    maskWidth = tensorDims[1];
                }
                else
                {
                    // Fallback: asumir cuadrado
                    int sqrtLen = (int)Math.Sqrt(maskTensor.Length);
                    if (sqrtLen <= 0)
                        throw new ArgumentException("Invalid tensor length");
                    maskWidth = maskHeight = sqrtLen;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error procesando dimensiones del tensor: {ex.Message}");
                // Fallback seguro: crear máscara vacía
                mask.Erase(SkiaSharp.SKColors.Transparent);
                return mask;
            }

            unsafe
            {
                var maskPtr = (byte*)mask.GetPixels();
                var stride = mask.RowBytes;

                for (int y = 0; y < originalHeight; y++)
                {
                    for (int x = 0; x < originalWidth; x++)
                    {
                        // Interpolación bilineal
                        float fx = (x * maskWidth) / (float)originalWidth;
                        float fy = (y * maskHeight) / (float)originalHeight;
                        
                        int x0 = (int)Math.Floor(fx);
                        int y0 = (int)Math.Floor(fy);
                        int x1 = Math.Min(x0 + 1, maskWidth - 1);
                        int y1 = Math.Min(y0 + 1, maskHeight - 1);
                        
                        float dx = fx - x0;
                        float dy = fy - y0;
                        
                        // Obtener valores de los 4 puntos
                        float v00 = GetTensorValue(maskTensor, tensorOffset, maskWidth, x0, y0);
                        float v10 = GetTensorValue(maskTensor, tensorOffset, maskWidth, x1, y0);
                        float v01 = GetTensorValue(maskTensor, tensorOffset, maskWidth, x0, y1);
                        float v11 = GetTensorValue(maskTensor, tensorOffset, maskWidth, x1, y1);
                        
                        // Interpolación bilineal
                        float v0 = v00 * (1 - dx) + v10 * dx;
                        float v1 = v01 * (1 - dx) + v11 * dx;
                        float value = v0 * (1 - dy) + v1 * dy;
                        
                        // Clamp a [0, 1] y convertir a [0, 255]
                        value = Math.Max(0.0f, Math.Min(1.0f, value));
                        maskPtr[y * stride + x] = (byte)(value * 255);
                    }
                }
            }

            return mask;
        }

        /// <summary>
        /// Obtiene valor del tensor en posición (x, y) con offset
        /// </summary>
        private float GetTensorValue(Tensor<float> tensor, int offset, int width, int x, int y)
        {
            try
            {
                if (x < 0 || x >= width || y < 0)
                    return 0.0f;
                
                int index = offset + y * width + x;
                if (index < 0 || index >= tensor.Length)
                    return 0.0f;
                
                return tensor[index];
            }
            catch
            {
                return 0.0f; // Fallback seguro
            }
        }

        /// <summary>
        /// Aplica la máscara alpha continua a la imagen original con premultiplied alpha correcto
        /// Original format: BGRA8888 Premul (confirmado - SkiaSharp desde BitmapSource)
        /// </summary>
        public SKBitmap ApplyMask(SKBitmap original, SKBitmap mask)
        {
            // Resultado: RGBA8888 Premul (alpha continuo, no binario)
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
                        // Alpha de la máscara (0-255)
                        byte maskAlpha = maskPtr[y * maskStride + x];
                        float alphaF = maskAlpha / 255.0f;
                        
                        uint originalPixel = originalPtr[y * originalStride + x];
                        
                        // Extraer componentes BGRA del original (formato confirmado: BGRA8888 Premul)
                        // No usar SKColor por pixel (performance)
                        byte origB = (byte)(originalPixel & 0xFF);
                        byte origG = (byte)((originalPixel >> 8) & 0xFF);
                        byte origR = (byte)((originalPixel >> 16) & 0xFF);
                        byte origA = (byte)((originalPixel >> 24) & 0xFF);
                        
                        // Unpremultiply: convertir de premul a straight RGB
                        // Evitar inestabilidad: si origA < 5, tratar RGB como 0
                        float r, g, b;
                        if (origA >= 5 && origA > 0)
                        {
                            float origAF = origA / 255.0f;
                            // Evitar división por cero (aunque ya verificamos origA > 0)
                            if (origAF > 0.0001f)
                            {
                                r = (origR / 255.0f) / origAF;
                                g = (origG / 255.0f) / origAF;
                                b = (origB / 255.0f) / origAF;
                                
                                // Clamp RGB a [0,1] para evitar valores fuera de rango por ruido
                                r = Math.Max(0.0f, Math.Min(1.0f, r));
                                g = Math.Max(0.0f, Math.Min(1.0f, g));
                                b = Math.Max(0.0f, Math.Min(1.0f, b));
                            }
                            else
                            {
                                r = g = b = 0.0f;
                            }
                        }
                        else
                        {
                            // Alpha muy bajo: tratar RGB como 0 para evitar división ruidosa
                            r = g = b = 0.0f;
                        }
                        
                        // Aplicar nuevo alpha de la máscara y premultiply
                        // Garantizar que final RGB está premultiplicado por maskAlpha
                        byte finalR = (byte)(r * alphaF * 255);
                        byte finalG = (byte)(g * alphaF * 255);
                        byte finalB = (byte)(b * alphaF * 255);
                        byte finalA = maskAlpha;
                        
                        // Construir píxel premultiplicado: BGRA
                        resultPtr[y * resultStride + x] = (uint)((finalA << 24) | (finalB << 16) | (finalG << 8) | finalR);
                    }
                }
            }

            return result;
        }

        public void Dispose()
        {
            _session?.Dispose();
            _session = null;
            _isInitialized = false;
        }
    }
}

