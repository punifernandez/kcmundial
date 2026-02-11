using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;

namespace KCMundial.Services
{
    public class CollageService
    {
        // Dimensiones en píxeles (tamaño real)
        // Strip vertical 3 fotos: Canvas 591x1772 px
        private const int STRIP_WIDTH = 591;   // ancho total
        private const int STRIP_HEIGHT = 1772;  // alto total
        private const int DPI = 300;
        
        // Strip vertical 3 fotos: Márgenes superior 180px, inferior 180px, laterales 50px
        private const int STRIP_TOP_MARGIN = 180;
        private const int STRIP_BOTTOM_MARGIN = 180;
        private const int STRIP_SIDE_MARGIN = 50;
        
        // Strip vertical 3 fotos: Área útil 491x1412 px
        // Cada foto ratio horizontal 4:3, tamaño 491x368 px
        private const int PHOTO_WIDTH = 491;
        private const int PHOTO_HEIGHT = 368;
        
        // Strip vertical 3 fotos: Posiciones Y=294, 702, 1110 (bloque centrado verticalmente)
        private const int PHOTO_Y_1 = 294;  // primera foto
        private const int PHOTO_Y_2 = 702;  // segunda foto
        private const int PHOTO_Y_3 = 1110;  // tercera foto

        public async Task<string> CreateStripAsync(List<string> photoPaths, string outputFolder, string? framePath = null)
        {
            return await Task.Run(() =>
            {
                if (photoPaths == null || photoPaths.Count == 0)
                {
                    throw new ArgumentException("No hay fotos para crear el collage");
                }

                var outputPath = Path.Combine(outputFolder, "strip.jpg");

                // Asegurar que el directorio existe
                Directory.CreateDirectory(outputFolder);

                SKSurface? surface = null;
                SKBitmap? frameBackground = null;
                
                try
                {
                    // Usar Rgba8888 en lugar de Rgb888x para mejor compatibilidad
                    var imageInfo = new SKImageInfo(STRIP_WIDTH, STRIP_HEIGHT, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    surface = SKSurface.Create(imageInfo);
                    if (surface == null)
                    {
                        throw new Exception("No se pudo crear la superficie para el collage");
                    }

                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.White);

                    // Load frame background if provided or exists (debe ser del tamaño completo del collage: 600x1800)
                    string? actualFramePath = framePath;
                    if (string.IsNullOrEmpty(actualFramePath))
                    {
                        // Fallback al frame por defecto si no se especifica uno
                        var exeDir = AppContext.BaseDirectory;
                        actualFramePath = Path.Combine(exeDir, "Assets", "frame.png");
                    }
                    
                    if (!string.IsNullOrEmpty(actualFramePath) && File.Exists(actualFramePath))
                    {
                        try
                    {
                        using (var stream = File.OpenRead(framePath))
                            {
                                frameBackground = SKBitmap.Decode(stream);
                            }
                        }
                        catch
                        {
                            // Ignore frame loading errors
                            frameBackground = null;
                        }
                    }

                    // Dibujar el frame como FONDO completo primero (si existe)
                    if (frameBackground != null && !frameBackground.IsNull)
                    {
                        try
                        {
                            // Escalar el frame al tamaño completo del collage
                            var scaledFrame = frameBackground.Resize(new SKImageInfo(STRIP_WIDTH, STRIP_HEIGHT), SKFilterQuality.High);
                            if (scaledFrame != null && !scaledFrame.IsNull)
                            {
                                canvas.DrawBitmap(scaledFrame, new SKRect(0, 0, STRIP_WIDTH, STRIP_HEIGHT));
                                scaledFrame.Dispose();
                            }
                        }
                        catch
                        {
                            // Si falla, usar fondo blanco
                            canvas.Clear(SKColors.White);
                        }
                    }
                    else
                    {
                        // Draw header area (for logo/title) - solo si no hay frame
                        // El header ahora es el margen superior de 180px
                        var headerRect = new SKRect(0, 0, STRIP_WIDTH, STRIP_TOP_MARGIN);
                        canvas.DrawRect(headerRect, new SKPaint { Color = SKColors.White });
                        
                        // Dibujar footer blanco
                        var footerRect = new SKRect(0, STRIP_HEIGHT - STRIP_BOTTOM_MARGIN, STRIP_WIDTH, STRIP_HEIGHT);
                        canvas.DrawRect(footerRect, new SKPaint { Color = SKColors.White });
                    }

                    // Posiciones Y fijas: 294, 702, 1110 (bloque centrado verticalmente)
                    var photoYPositions = new[] { PHOTO_Y_1, PHOTO_Y_2, PHOTO_Y_3 };
                    // Centrar horizontalmente: las fotos empiezan en el margen lateral (50px)
                    var photoStartX = STRIP_SIDE_MARGIN; // Las fotos están centradas, empiezan en el margen lateral

                    // Draw photos (formato vertical: 3 fotos una debajo de la otra)
                    for (int i = 0; i < photoPaths.Count && i < 3; i++)
                    {
                        var photoY = photoYPositions[i];

                        if (!string.IsNullOrEmpty(photoPaths[i]) && File.Exists(photoPaths[i]))
                        {
                            try
                            {
                                using (var stream = File.OpenRead(photoPaths[i]))
                                {
                                    using (var bitmap = SKBitmap.Decode(stream))
                                    {
                                        if (bitmap != null && !bitmap.IsNull)
                                        {
                                            // Forzar que todas las fotos tengan exactamente el mismo tamaño
                                            // Usar crop-to-fill: escalar para llenar el espacio y recortar si es necesario
                                            var originalAspectRatio = (float)bitmap.Width / bitmap.Height;
                                            var targetAspectRatio = 4.0f / 3.0f; // Ratio horizontal 4:3 (491/368 ≈ 1.334)
                                            
                                            float sourceX = 0, sourceY = 0, sourceWidth = bitmap.Width, sourceHeight = bitmap.Height;
                                            
                                            // Calcular qué parte de la foto original usar (crop)
                                            if (originalAspectRatio > targetAspectRatio)
                                            {
                                                // Foto original es más ancha - recortar los lados
                                                sourceHeight = bitmap.Height;
                                                sourceWidth = bitmap.Height * targetAspectRatio;
                                                sourceX = (bitmap.Width - sourceWidth) / 2;
                                                sourceY = 0;
                                            }
                                            else
                                            {
                                                // Foto original es más alta - recortar arriba/abajo
                                                sourceWidth = bitmap.Width;
                                                sourceHeight = bitmap.Width / targetAspectRatio;
                                                sourceX = 0;
                                                sourceY = (bitmap.Height - sourceHeight) / 2;
                                            }
                                            
                                            // Crear un bitmap recortado
                                            var croppedBitmap = new SKBitmap((int)sourceWidth, (int)sourceHeight);
                                            if (bitmap.ExtractSubset(croppedBitmap, new SKRectI((int)sourceX, (int)sourceY, (int)(sourceX + sourceWidth), (int)(sourceY + sourceHeight))))
                                            {
                                                // Escalar al tamaño exacto del espacio disponible
                                                var scaledBitmap = croppedBitmap.Resize(new SKImageInfo(PHOTO_WIDTH, PHOTO_HEIGHT), SKFilterQuality.High);
                                                if (scaledBitmap != null && !scaledBitmap.IsNull)
                                                {
                                                    // Dibujar exactamente en el espacio asignado
                                                    var photoRect = new SKRect(
                                                        photoStartX, 
                                                        photoY, 
                                                        photoStartX + PHOTO_WIDTH, 
                                                        photoY + PHOTO_HEIGHT);
                                                    canvas.DrawBitmap(scaledBitmap, photoRect);
                                                    scaledBitmap.Dispose();
                                                }
                                                croppedBitmap.Dispose();
                                            }
                                            else
                                            {
                                                croppedBitmap.Dispose();
                                                // Fallback: escalar normal si el crop falla
                                        var scaledBitmap = bitmap.Resize(new SKImageInfo(PHOTO_WIDTH, PHOTO_HEIGHT), SKFilterQuality.High);
                                                if (scaledBitmap != null && !scaledBitmap.IsNull)
                                                {
                                                    var photoRect = new SKRect(photoStartX, photoY, photoStartX + PHOTO_WIDTH, photoY + PHOTO_HEIGHT);
                                        canvas.DrawBitmap(scaledBitmap, photoRect);
                                        scaledBitmap.Dispose();
                                    }
                                }
                            }
                        }
                                }
                            }
                            catch (Exception ex)
                            {
                                // Log error but continue with other photos
                                System.Diagnostics.Debug.WriteLine($"Error al cargar foto {i}: {ex.Message}");
                            }
                        }
                    }

                    // Draw footer with date/time (solo si no hay frame de fondo)
                    // El footer ahora está en el margen inferior de 150px
                    if (frameBackground == null)
                    {
                        var footerRect = new SKRect(0, STRIP_HEIGHT - STRIP_BOTTOM_MARGIN, STRIP_WIDTH, STRIP_HEIGHT);
                    canvas.DrawRect(footerRect, new SKPaint { Color = SKColors.White });

                    var dateTimeText = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    using (var paint = new SKPaint
                    {
                        Color = SKColors.Black,
                        TextSize = 24,
                        IsAntialias = true,
                        TextAlign = SKTextAlign.Center
                    })
                    {
                            var textY = STRIP_HEIGHT - STRIP_BOTTOM_MARGIN / 2 + paint.TextSize / 3;
                        canvas.DrawText(dateTimeText, STRIP_WIDTH / 2, textY, paint);
                        }
                    }

                    // Save image - Forzar renderizado completo
                    canvas.Flush();
                    
                    SKImage? image = null;
                    try
                    {
                        image = surface.Snapshot();
                        if (image == null)
                        {
                            throw new Exception("No se pudo crear la imagen del collage");
                        }

                        // Intentar codificar con diferentes métodos
                        SKData? data = null;
                        Exception? lastException = null;
                        
                        // Método 1: Encode JPEG con calidad 90
                        try
                        {
                            data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                        }
                        catch (Exception ex1)
                        {
                            lastException = ex1;
                            // Método 2: Encode JPEG con calidad 85
                            try
                            {
                                data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                            }
                            catch (Exception ex2)
                            {
                                lastException = ex2;
                                // Método 3: Usar PNG como fallback
                                try
                                {
                                    outputPath = Path.Combine(outputFolder, "strip.png");
                                    data = image.Encode(SKEncodedImageFormat.Png, 100);
                                }
                                catch (Exception ex3)
                                {
                                    throw new Exception($"No se pudo codificar la imagen. JPEG (90): {ex1.Message}, JPEG (85): {ex2.Message}, PNG: {ex3.Message}");
                                }
                            }
                        }

                        if (data == null)
                        {
                            throw new Exception("No se pudo codificar la imagen del collage - data es null");
                        }

                        // Asegurar que el archivo se cierre correctamente
                        using (data)
                        {
                            byte[] bytes = data.ToArray();
                            if (bytes == null || bytes.Length == 0)
                            {
                                throw new Exception("Los datos codificados están vacíos");
                            }
                            
                            // Guardar con metadatos DPI correctos usando System.Drawing
                            using (var ms = new MemoryStream(bytes))
                            {
                                using (var bitmap = new Bitmap(ms))
                                {
                                    // Establecer resolución a 300 DPI
                                    bitmap.SetResolution(DPI, DPI);
                                    
                                    // Guardar con metadatos DPI
                                    var encoder = ImageCodecInfo.GetImageEncoders()
                                        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                                    
                                    if (encoder != null)
                                    {
                                        var encoderParams = new EncoderParameters(1);
                                        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                                        
                                        bitmap.Save(outputPath, encoder, encoderParams);
                                        encoderParams.Dispose();
                                    }
                                    else
                                    {
                                        // Fallback: guardar sin encoder específico
                                        bitmap.Save(outputPath, ImageFormat.Jpeg);
                                    }
                                }
                            }
                            
                            System.Diagnostics.Debug.WriteLine($"Guardando strip: {outputPath} (300 DPI)");
                            System.Diagnostics.Debug.WriteLine($"Strip guardado exitosamente");
                        }
                    }
                    finally
                    {
                        image?.Dispose();
                    }

                    // Verificar que el archivo se creó correctamente
                    if (!File.Exists(outputPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"ERROR: El archivo del collage no existe después de guardar: {outputPath}");
                        throw new Exception("El archivo del collage no se creó correctamente");
                    }

                    var fileInfo = new FileInfo(outputPath);
                    System.Diagnostics.Debug.WriteLine($"✓✓✓ STRIP CREADO EXITOSAMENTE: {outputPath} ({fileInfo.Length} bytes)");
                    return outputPath;
                }
                finally
                {
                    frameBackground?.Dispose();
                    surface?.Dispose();
                }
            });
        }

        public async Task<string> CreateInstantPhotoAsync(List<string> photoPaths, string outputFolder, string? framePath = null)
        {
            return await Task.Run(() =>
            {
                if (photoPaths == null || photoPaths.Count == 0)
                {
                    throw new ArgumentException("No hay fotos para crear la instantánea");
                }

                var outputPath = Path.Combine(outputFolder, "instant.jpg");
                Directory.CreateDirectory(outputFolder);

                    // Instantánea: Canvas 1063x1063 px
                const int IMAGE_SIZE = 1063;
                // Márgenes laterales 60 px, superior 60 px, inferior 295 px
                // Foto ubicada en (60,60) tamaño 943x708 px
                const int PHOTO_X = 60;
                const int PHOTO_Y = 60;
                const int PHOTO_WIDTH = 943;
                const int PHOTO_HEIGHT = 708;
                // Margen inferior reservado 295 px para texto
                const int BOTTOM_MARGIN = 295;

                SKSurface? surface = null;
                SKBitmap? frameBackground = null;

                try
                {
                    var imageInfo = new SKImageInfo(IMAGE_SIZE, IMAGE_SIZE, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    surface = SKSurface.Create(imageInfo);
                    if (surface == null)
                    {
                        throw new Exception("No se pudo crear la superficie");
                    }

                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.White);

                    // Cargar frame si existe
                    string? actualFramePath = framePath;
                    if (!string.IsNullOrEmpty(actualFramePath) && File.Exists(actualFramePath))
                    {
                        try
                        {
                            using (var stream = File.OpenRead(actualFramePath))
                            {
                                frameBackground = SKBitmap.Decode(stream);
                            }
                        }
                        catch
                        {
                            frameBackground = null;
                        }
                    }

                    if (frameBackground != null && !frameBackground.IsNull)
                    {
                        var scaledFrame = frameBackground.Resize(new SKImageInfo(IMAGE_SIZE, IMAGE_SIZE), SKFilterQuality.High);
                        if (scaledFrame != null && !scaledFrame.IsNull)
                        {
                            canvas.DrawBitmap(scaledFrame, new SKRect(0, 0, IMAGE_SIZE, IMAGE_SIZE));
                            scaledFrame.Dispose();
                        }
                    }

                    // Dibujar foto (crop-to-fill)
                    if (File.Exists(photoPaths[0]))
                    {
                        using (var stream = File.OpenRead(photoPaths[0]))
                        {
                            using (var bitmap = SKBitmap.Decode(stream))
                            {
                                   if (bitmap != null && !bitmap.IsNull)
                                   {
                                       // Crop-to-fill para tamaño exacto 945x709
                                       var targetAspectRatio = (float)PHOTO_WIDTH / PHOTO_HEIGHT; // 945/709
                                       var originalAspectRatio = (float)bitmap.Width / bitmap.Height;
                                    
                                    float sourceX = 0, sourceY = 0, sourceWidth = bitmap.Width, sourceHeight = bitmap.Height;
                                    
                                    if (originalAspectRatio > targetAspectRatio)
                                    {
                                        sourceHeight = bitmap.Height;
                                        sourceWidth = bitmap.Height * targetAspectRatio;
                                        sourceX = (bitmap.Width - sourceWidth) / 2;
                                    }
                                    else
                                    {
                                        sourceWidth = bitmap.Width;
                                        sourceHeight = bitmap.Width / targetAspectRatio;
                                        sourceY = (bitmap.Height - sourceHeight) / 2;
                                    }
                                    
                                    var croppedBitmap = new SKBitmap((int)sourceWidth, (int)sourceHeight);
                                    if (bitmap.ExtractSubset(croppedBitmap, new SKRectI((int)sourceX, (int)sourceY, (int)(sourceX + sourceWidth), (int)(sourceY + sourceHeight))))
                                    {
                                        var scaledBitmap = croppedBitmap.Resize(new SKImageInfo(PHOTO_WIDTH, PHOTO_HEIGHT), SKFilterQuality.High);
                                        if (scaledBitmap != null && !scaledBitmap.IsNull)
                                        {
                                            // Foto en (59,59) tamaño 945x709 px
                                            var photoRect = new SKRect(PHOTO_X, PHOTO_Y, PHOTO_X + PHOTO_WIDTH, PHOTO_Y + PHOTO_HEIGHT);
                                            canvas.DrawBitmap(scaledBitmap, photoRect);
                                            scaledBitmap.Dispose();
                                        }
                                        croppedBitmap.Dispose();
                                    }
                                    else
                                    {
                                        croppedBitmap.Dispose();
                                        var scaledBitmap = bitmap.Resize(new SKImageInfo(PHOTO_WIDTH, PHOTO_HEIGHT), SKFilterQuality.High);
                                        if (scaledBitmap != null && !scaledBitmap.IsNull)
                                        {
                                            // Foto en (59,59) tamaño 945x709 px
                                            var photoRect = new SKRect(PHOTO_X, PHOTO_Y, PHOTO_X + PHOTO_WIDTH, PHOTO_Y + PHOTO_HEIGHT);
                                            canvas.DrawBitmap(scaledBitmap, photoRect);
                                            scaledBitmap.Dispose();
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Guardar imagen
                    canvas.Flush();
                    SKImage? image = null;
                    try
                    {
                        image = surface.Snapshot();
                        if (image == null)
                        {
                            throw new Exception("No se pudo crear la imagen");
                        }

                        SKData? data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                        if (data == null)
                        {
                            throw new Exception("No se pudo codificar la imagen");
                        }

                        using (data)
                        {
                            byte[] bytes = data.ToArray();
                            
                            // Guardar con DPI correcto
                            using (var ms = new MemoryStream(bytes))
                            {
                                using (var bitmap = new Bitmap(ms))
                                {
                                    bitmap.SetResolution(DPI, DPI);
                                    var encoder = ImageCodecInfo.GetImageEncoders()
                                        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                                    
                                    if (encoder != null)
                                    {
                                        var encoderParams = new EncoderParameters(1);
                                        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                                        bitmap.Save(outputPath, encoder, encoderParams);
                                        encoderParams.Dispose();
                                    }
                                    else
                                    {
                                        bitmap.Save(outputPath, ImageFormat.Jpeg);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        image?.Dispose();
                    }

                    return outputPath;
                }
                finally
                {
                    frameBackground?.Dispose();
                    surface?.Dispose();
                }
            });
        }

        public async Task<string> CreateTwoPhotosHorizontalAsync(List<string> photoPaths, string outputFolder, string? framePath = null)
        {
            return await Task.Run(() =>
            {
                if (photoPaths == null || photoPaths.Count < 2)
                {
                    throw new ArgumentException("Se necesitan al menos 2 fotos para el diseño horizontal");
                }

                var outputPath = Path.Combine(outputFolder, "two_photos.jpg");
                Directory.CreateDirectory(outputFolder);

                // Dos fotos horizontal: Canvas 1772x591 px
                const int STRIP_WIDTH = 1772;   // ancho
                const int STRIP_HEIGHT = 591;  // alto
                
                // Márgenes 50px en todos los lados
                const int HORIZONTAL_MARGIN = 50;
                
                // Área útil: 1672x491 px dividida en tres bloques iguales
                const int USABLE_WIDTH = 1672;  // 1772 - 50*2
                const int USABLE_HEIGHT = 491;  // 591 - 50*2
                const int BLOCK_WIDTH = 557;  // 1672 / 3 ≈ 557px
                
                // Fotos izquierda y derecha: 557x491 px
                const int PHOTO_WIDTH = 557;
                const int PHOTO_HEIGHT = 491;
                
                // Posiciones X de los bloques (con margen de 50px)
                const int LEFT_PHOTO_X = HORIZONTAL_MARGIN;  // Foto izquierda: 50px
                const int TEXT_BLOCK_X = HORIZONTAL_MARGIN + BLOCK_WIDTH;  // Bloque central: 607px
                const int RIGHT_PHOTO_X = HORIZONTAL_MARGIN + (BLOCK_WIDTH * 2);  // Foto derecha: 1164px
                const int PHOTO_Y = HORIZONTAL_MARGIN;  // Y común para todas las fotos

                SKSurface? surface = null;
                SKBitmap? frameBackground = null;

                try
                {
                    var imageInfo = new SKImageInfo(STRIP_WIDTH, STRIP_HEIGHT, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    surface = SKSurface.Create(imageInfo);
                    if (surface == null)
                    {
                        throw new Exception("No se pudo crear la superficie");
                    }

                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.White);

                    // Cargar frame si existe
                    string? actualFramePath = framePath;
                    if (!string.IsNullOrEmpty(actualFramePath) && File.Exists(actualFramePath))
                    {
                        try
                        {
                            using (var stream = File.OpenRead(actualFramePath))
                            {
                                frameBackground = SKBitmap.Decode(stream);
                            }
                        }
                        catch
                        {
                            frameBackground = null;
                        }
                    }

                    if (frameBackground != null && !frameBackground.IsNull)
                    {
                        var scaledFrame = frameBackground.Resize(new SKImageInfo(STRIP_WIDTH, STRIP_HEIGHT), SKFilterQuality.High);
                        if (scaledFrame != null && !scaledFrame.IsNull)
                        {
                            canvas.DrawBitmap(scaledFrame, new SKRect(0, 0, STRIP_WIDTH, STRIP_HEIGHT));
                            scaledFrame.Dispose();
                        }
                    }

                    // Dibujar 2 fotos: izquierda y derecha, con bloque central de texto
                    var photoPositions = new[] { LEFT_PHOTO_X, RIGHT_PHOTO_X };

                    for (int i = 0; i < 2 && i < photoPaths.Count; i++)
                    {
                        var photoX = photoPositions[i];
                        var photoY = PHOTO_Y;

                        if (File.Exists(photoPaths[i]))
                        {
                            using (var stream = File.OpenRead(photoPaths[i]))
                            {
                                using (var bitmap = SKBitmap.Decode(stream))
                                {
                                    if (bitmap != null && !bitmap.IsNull)
                                    {
                                        // Crop-to-fill para 591x591 (cuadrado)
                                        var targetAspectRatio = 1.0f; // Cuadrado
                                        var originalAspectRatio = (float)bitmap.Width / bitmap.Height;
                                        
                                        float sourceX = 0, sourceY = 0, sourceWidth = bitmap.Width, sourceHeight = bitmap.Height;
                                        
                                        if (originalAspectRatio > targetAspectRatio)
                                        {
                                            sourceHeight = bitmap.Height;
                                            sourceWidth = bitmap.Height * targetAspectRatio;
                                            sourceX = (bitmap.Width - sourceWidth) / 2;
                                        }
                                        else
                                        {
                                            sourceWidth = bitmap.Width;
                                            sourceHeight = bitmap.Width / targetAspectRatio;
                                            sourceY = (bitmap.Height - sourceHeight) / 2;
                                        }
                                        
                                        var croppedBitmap = new SKBitmap((int)sourceWidth, (int)sourceHeight);
                                        if (bitmap.ExtractSubset(croppedBitmap, new SKRectI((int)sourceX, (int)sourceY, (int)(sourceX + sourceWidth), (int)(sourceY + sourceHeight))))
                                        {
                                            var scaledBitmap = croppedBitmap.Resize(new SKImageInfo(PHOTO_WIDTH, PHOTO_HEIGHT), SKFilterQuality.High);
                                            if (scaledBitmap != null && !scaledBitmap.IsNull)
                                            {
                                                var photoRect = new SKRect(photoX, photoY, photoX + PHOTO_WIDTH, photoY + PHOTO_HEIGHT);
                                                canvas.DrawBitmap(scaledBitmap, photoRect);
                                                scaledBitmap.Dispose();
                                            }
                                            croppedBitmap.Dispose();
                                        }
                                        else
                                        {
                                            croppedBitmap.Dispose();
                                            var scaledBitmap = bitmap.Resize(new SKImageInfo(PHOTO_WIDTH, PHOTO_HEIGHT), SKFilterQuality.High);
                                            if (scaledBitmap != null && !scaledBitmap.IsNull)
                                            {
                                                var photoRect = new SKRect(photoX, photoY, photoX + PHOTO_WIDTH, photoY + PHOTO_HEIGHT);
                                                canvas.DrawBitmap(scaledBitmap, photoRect);
                                                scaledBitmap.Dispose();
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Guardar imagen
                    canvas.Flush();
                    SKImage? image = null;
                    try
                    {
                        image = surface.Snapshot();
                        if (image == null)
                        {
                            throw new Exception("No se pudo crear la imagen");
                        }

                        SKData? data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                        if (data == null)
                        {
                            throw new Exception("No se pudo codificar la imagen");
                        }

                        using (data)
                        {
                            byte[] bytes = data.ToArray();
                            
                            // Guardar con DPI correcto
                            using (var ms = new MemoryStream(bytes))
                            {
                                using (var bitmap = new Bitmap(ms))
                                {
                                    bitmap.SetResolution(DPI, DPI);
                                    var encoder = ImageCodecInfo.GetImageEncoders()
                                        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                                    
                                    if (encoder != null)
                                    {
                                        var encoderParams = new EncoderParameters(1);
                                        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                                        bitmap.Save(outputPath, encoder, encoderParams);
                                        encoderParams.Dispose();
                                    }
                                    else
                                    {
                                        bitmap.Save(outputPath, ImageFormat.Jpeg);
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        image?.Dispose();
                }

                return outputPath;
                }
                finally
                {
                    frameBackground?.Dispose();
                    surface?.Dispose();
                }
            });
        }

        /// <summary>
        /// Crea un collage de figurita autoadhesiva de 4.5 cm x 6 cm (531x709 px a 300 DPI)
        /// </summary>
        public async Task<string> CreateStickerAsync(string photoPath, string outputFolder, string? backgroundPath = null)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrEmpty(photoPath) || !File.Exists(photoPath))
                {
                    throw new ArgumentException("No hay foto para crear la figurita");
                }

                var outputPath = Path.Combine(outputFolder, "sticker.jpg");
                Directory.CreateDirectory(outputFolder);

                // Dimensiones: 4.5 cm x 6 cm a 300 DPI
                // 4.5 cm = 1.77 pulgadas × 300 = 531 píxeles
                // 6 cm = 2.36 pulgadas × 300 = 709 píxeles
                const int STICKER_WIDTH = 531;   // 4.5 cm
                const int STICKER_HEIGHT = 709;  // 6 cm
                const int DPI = 300;

                SKSurface? surface = null;
                SKBitmap? backgroundBitmap = null;

                try
                {
                    var imageInfo = new SKImageInfo(STICKER_WIDTH, STICKER_HEIGHT, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    surface = SKSurface.Create(imageInfo);
                    if (surface == null)
                    {
                        throw new Exception("No se pudo crear la superficie para la figurita");
                    }

                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.White);

                    // Buscar el background de figurita
                    string? actualBackgroundPath = backgroundPath;
                    if (string.IsNullOrEmpty(actualBackgroundPath))
                    {
                        // Buscar en múltiples ubicaciones
                        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                        var possiblePaths = new[]
                        {
                            Path.Combine(desktop, "KCMundial", "Assets", "background.png"),
                            Path.Combine(desktop, "KCMundial", "Assets", "figurita.png"),
                            Path.Combine(desktop, "KCMundial", "Assets", "sticker.png"),
                            Path.Combine(AppContext.BaseDirectory, "Assets", "background.png"),
                            Path.Combine(AppContext.BaseDirectory, "Assets", "figurita.png"),
                            Path.Combine(AppContext.BaseDirectory, "Assets", "sticker.png")
                        };

                        foreach (var path in possiblePaths)
                        {
                            if (File.Exists(path))
                            {
                                actualBackgroundPath = path;
                                System.Diagnostics.Debug.WriteLine($"✓ Background encontrado: {path}");
                                break;
                            }
                        }
                    }

                    // Cargar y dibujar el background si existe
                    if (!string.IsNullOrEmpty(actualBackgroundPath) && File.Exists(actualBackgroundPath))
                    {
                        try
                        {
                            using (var stream = File.OpenRead(actualBackgroundPath))
                            {
                                backgroundBitmap = SKBitmap.Decode(stream);
                            }

                            if (backgroundBitmap != null && !backgroundBitmap.IsNull)
                            {
                                // Escalar el background al tamaño completo de la figurita
                                var scaledBackground = backgroundBitmap.Resize(new SKImageInfo(STICKER_WIDTH, STICKER_HEIGHT), SKFilterQuality.High);
                                if (scaledBackground != null && !scaledBackground.IsNull)
                                {
                                    canvas.DrawBitmap(scaledBackground, new SKRect(0, 0, STICKER_WIDTH, STICKER_HEIGHT));
                                    scaledBackground.Dispose();
                                    System.Diagnostics.Debug.WriteLine($"✓ Background dibujado: {STICKER_WIDTH}x{STICKER_HEIGHT}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error al cargar background: {ex.Message}");
                            // Continuar sin background si falla
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("⚠ No se encontró background, usando fondo blanco");
                    }

                    // Dibujar la foto sobre el background
                    // La foto ocupará la mayor parte del espacio disponible
                    // Dejaremos márgenes pequeños para que el background sea visible
                    const int PHOTO_MARGIN = 20; // Márgenes de 20px en todos los lados
                    const int PHOTO_WIDTH = STICKER_WIDTH - (PHOTO_MARGIN * 2);  // 491 px
                    const int PHOTO_HEIGHT = STICKER_HEIGHT - (PHOTO_MARGIN * 2); // 669 px
                    const int PHOTO_X = PHOTO_MARGIN;
                    const int PHOTO_Y = PHOTO_MARGIN;

                    if (File.Exists(photoPath))
                    {
                        using (var stream = File.OpenRead(photoPath))
                        {
                            using (var bitmap = SKBitmap.Decode(stream))
                            {
                                if (bitmap != null && !bitmap.IsNull)
                                {
                                    // Verificar si la imagen tiene canal alpha (transparencia)
                                    bool hasAlpha = bitmap.AlphaType != SKAlphaType.Opaque;
                                    
                                    // Crop-to-fill para ajustar la foto al espacio disponible
                                    var targetAspectRatio = (float)PHOTO_WIDTH / PHOTO_HEIGHT;
                                    var originalAspectRatio = (float)bitmap.Width / bitmap.Height;

                                    float sourceX = 0, sourceY = 0, sourceWidth = bitmap.Width, sourceHeight = bitmap.Height;

                                    if (originalAspectRatio > targetAspectRatio)
                                    {
                                        // Foto original es más ancha - recortar los lados
                                        sourceHeight = bitmap.Height;
                                        sourceWidth = bitmap.Height * targetAspectRatio;
                                        sourceX = (bitmap.Width - sourceWidth) / 2;
                                    }
                                    else
                                    {
                                        // Foto original es más alta - recortar arriba/abajo
                                        sourceWidth = bitmap.Width;
                                        sourceHeight = bitmap.Width / targetAspectRatio;
                                        sourceY = (bitmap.Height - sourceHeight) / 2;
                                    }

                                    var croppedBitmap = new SKBitmap((int)sourceWidth, (int)sourceHeight);
                                    if (bitmap.ExtractSubset(croppedBitmap, new SKRectI((int)sourceX, (int)sourceY, (int)(sourceX + sourceWidth), (int)(sourceY + sourceHeight))))
                                    {
                                        // Asegurar que el bitmap escalado mantenga el formato RGBA si tiene transparencia
                                        var scaledInfo = new SKImageInfo(PHOTO_WIDTH, PHOTO_HEIGHT, 
                                            hasAlpha ? SKColorType.Rgba8888 : SKColorType.Rgb888x, 
                                            hasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque);
                                        
                                        var scaledBitmap = croppedBitmap.Resize(scaledInfo, SKFilterQuality.High);
                                        if (scaledBitmap != null && !scaledBitmap.IsNull)
                                        {
                                            var photoRect = new SKRect(PHOTO_X, PHOTO_Y, PHOTO_X + PHOTO_WIDTH, PHOTO_Y + PHOTO_HEIGHT);
                                            
                                            // Usar paint con modo de composición adecuado para respetar transparencia
                                            using (var paint = new SKPaint())
                                            {
                                                paint.IsAntialias = true;
                                                paint.FilterQuality = SKFilterQuality.High;
                                                // Si tiene transparencia, usar modo SrcOver para composición correcta
                                                if (hasAlpha)
                                                {
                                                    paint.BlendMode = SKBlendMode.SrcOver;
                                                }
                                                canvas.DrawBitmap(scaledBitmap, photoRect, paint);
                                            }
                                            
                                            scaledBitmap.Dispose();
                                            System.Diagnostics.Debug.WriteLine($"✓ Foto dibujada: {PHOTO_WIDTH}x{PHOTO_HEIGHT} en ({PHOTO_X}, {PHOTO_Y}), Alpha: {hasAlpha}");
                                        }
                                        croppedBitmap.Dispose();
                                    }
                                    else
                                    {
                                        croppedBitmap.Dispose();
                                        // Fallback: escalar normal si el crop falla
                                        var scaledInfo = new SKImageInfo(PHOTO_WIDTH, PHOTO_HEIGHT, 
                                            hasAlpha ? SKColorType.Rgba8888 : SKColorType.Rgb888x, 
                                            hasAlpha ? SKAlphaType.Premul : SKAlphaType.Opaque);
                                        
                                        var scaledBitmap = bitmap.Resize(scaledInfo, SKFilterQuality.High);
                                        if (scaledBitmap != null && !scaledBitmap.IsNull)
                                        {
                                            var photoRect = new SKRect(PHOTO_X, PHOTO_Y, PHOTO_X + PHOTO_WIDTH, PHOTO_Y + PHOTO_HEIGHT);
                                            
                                            using (var paint = new SKPaint())
                                            {
                                                paint.IsAntialias = true;
                                                paint.FilterQuality = SKFilterQuality.High;
                                                if (hasAlpha)
                                                {
                                                    paint.BlendMode = SKBlendMode.SrcOver;
                                                }
                                                canvas.DrawBitmap(scaledBitmap, photoRect, paint);
                                            }
                                            
                                            scaledBitmap.Dispose();
                                        }
                                    }
                                }
                            }
                        }
                    }

                    // Guardar imagen
                    canvas.Flush();
                    SKImage? image = null;
                    try
                    {
                        image = surface.Snapshot();
                        if (image == null)
                        {
                            throw new Exception("No se pudo crear la imagen de la figurita");
                        }

                        SKData? data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                        if (data == null)
                        {
                            throw new Exception("No se pudo codificar la imagen");
                        }

                        using (data)
                        {
                            byte[] bytes = data.ToArray();
                            if (bytes == null || bytes.Length == 0)
                            {
                                throw new Exception("Los datos codificados están vacíos");
                            }

                            // Guardar con metadatos DPI correctos usando System.Drawing
                            using (var ms = new MemoryStream(bytes))
                            {
                                using (var bitmap = new Bitmap(ms))
                                {
                                    // Establecer resolución a 300 DPI
                                    bitmap.SetResolution(DPI, DPI);

                                    // Guardar con metadatos DPI
                                    var encoder = ImageCodecInfo.GetImageEncoders()
                                        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

                                    if (encoder != null)
                                    {
                                        var encoderParams = new EncoderParameters(1);
                                        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 90L);

                                        bitmap.Save(outputPath, encoder, encoderParams);
                                        encoderParams.Dispose();
                                    }
                                    else
                                    {
                                        // Fallback: guardar sin encoder específico
                                        bitmap.Save(outputPath, ImageFormat.Jpeg);
                                    }
                                }
                            }

                            System.Diagnostics.Debug.WriteLine($"✓ Figurita guardada: {outputPath} (300 DPI, {STICKER_WIDTH}x{STICKER_HEIGHT} px = 4.5x6 cm)");
                        }
                    }
                    finally
                    {
                        image?.Dispose();
                    }

                    // Verificar que el archivo se creó correctamente
                    if (!File.Exists(outputPath))
                    {
                        throw new Exception("El archivo de la figurita no se creó correctamente");
                    }

                    var fileInfo = new FileInfo(outputPath);
                    System.Diagnostics.Debug.WriteLine($"✓✓✓ FIGURITA CREADA EXITOSAMENTE: {outputPath} ({fileInfo.Length} bytes)");
                    return outputPath;
                }
                finally
                {
                    backgroundBitmap?.Dispose();
                    surface?.Dispose();
                }
            });
        }
    }
}

