using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KCMundial;

namespace KCMundial.Services
{
    public class StorageService
    {
        // Path base fijo y determinístico
        public const string BasePath = @"C:\KCMundial";
        
        private static readonly string STRIPS_DIR = Path.Combine(BasePath, "Photos", "Strips");
        private static readonly string SHOTS_DIR = Path.Combine(BasePath, "Photos", "Shots");
        private static readonly string LOGS_DIR = Path.Combine(BasePath, "Logs");

        /// <summary>
        /// Inicializa las carpetas necesarias. Debe llamarse al inicio de la aplicación.
        /// </summary>
        public static void InitializeDirectories()
        {
            try
            {
                Directory.CreateDirectory(STRIPS_DIR);
                Directory.CreateDirectory(SHOTS_DIR);
                Directory.CreateDirectory(LOGS_DIR);
                CrashLogger.Log($"CAP_SAVE_ROOT: {BasePath}");
            }
            catch (Exception ex)
            {
                CrashLogger.Log($"CAP_SAVE_FAIL: Error al crear carpetas - {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Obtiene la ruta del directorio de strips.
        /// </summary>
        public static string GetStripsDirectory()
        {
            Directory.CreateDirectory(STRIPS_DIR);
            return STRIPS_DIR;
        }

        /// <summary>
        /// Obtiene la ruta del directorio de shots.
        /// </summary>
        public static string GetShotsDirectory()
        {
            Directory.CreateDirectory(SHOTS_DIR);
            return SHOTS_DIR;
        }

        public async Task<string> CreateSessionFolderAsync()
        {
            // Mantener compatibilidad con código existente, pero usar rutas fijas
            return await Task.Run(() =>
            {
                // Crear carpetas principales si no existen
                Directory.CreateDirectory(STRIPS_DIR);
                Directory.CreateDirectory(SHOTS_DIR);
                
                // Retornar una carpeta temporal para procesamiento (solo para archivos intermedios)
                var tempPath = Path.Combine(BasePath, "Photos", "temp", DateTime.Now.ToString("Session_HH-mm-ss"));
                Directory.CreateDirectory(tempPath);
                return tempPath;
            });
        }

        public async Task<string> SaveStripAsync(string sourceStripPath, string sessionFolder)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Usar ruta fija determinística: C:\KCMundial\Photos\Strips\
                    var stripsDir = GetStripsDirectory();
                    
                    // SIEMPRE crear directorio antes de guardar
                    Directory.CreateDirectory(stripsDir);
                    var dirExistsBefore = Directory.Exists(stripsDir);
                    CrashLogger.Log($"CAP_SAVE_STRIP: dirBefore={stripsDir}, dirExists={dirExistsBefore}");
                    
                    if (!dirExistsBefore)
                    {
                        var ex = new DirectoryNotFoundException($"No se pudo crear directorio: {stripsDir}");
                        CrashLogger.Log($"CAP_SAVE_FAIL: {ex.ToString()}", ex);
                        throw ex;
                    }
                    
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var stripFileName = $"{timestamp}_strip.png";
                    var stripDestPath = Path.Combine(stripsDir, stripFileName);
                    
                    CrashLogger.Log($"CAP_SAVE_STRIP: finalPath={stripDestPath}, sourceExists={File.Exists(sourceStripPath)}");
                    
                    if (File.Exists(sourceStripPath))
                    {
                        File.Copy(sourceStripPath, stripDestPath, overwrite: true);
                        
                        // Verificar que el archivo se escribió correctamente
                        var fileExistsAfter = File.Exists(stripDestPath);
                        if (fileExistsAfter)
                        {
                            var fileInfo = new FileInfo(stripDestPath);
                            CrashLogger.Log($"STRIP saved: {stripDestPath}");
                            CrashLogger.Log($"CAP_SAVE_STRIP: Guardado exitoso - finalPath={stripDestPath}, dirExists={Directory.Exists(stripsDir)}, File.Exists={fileExistsAfter}, size={fileInfo.Length} bytes");
                        }
                        else
                        {
                            var ex = new Exception($"Archivo no existe después de escribir: {stripDestPath}");
                            CrashLogger.Log($"CAP_SAVE_FAIL: {ex.ToString()}", ex);
                            throw ex;
                        }
                    }
                    else
                    {
                        var ex = new FileNotFoundException($"Archivo fuente no existe: {sourceStripPath}");
                        CrashLogger.Log($"CAP_SAVE_FAIL: {ex.ToString()}", ex);
                        throw ex;
                    }
                    
                    return stripDestPath;
                }
                catch (Exception ex)
                {
                    CrashLogger.Log($"CAP_SAVE_FAIL: Error en SaveStripAsync - {ex.ToString()}", ex);
                    throw; // NO tragar la excepción
                }
            });
        }

        public async Task<List<string>> SaveShotsAsync(List<string> photoPaths, string sessionFolder)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Usar ruta fija determinística
                    var shotsDir = GetShotsDirectory();
                    
                    var savedPaths = new List<string>();
                    
                    CrashLogger.Log($"SaveShotsAsync: Guardando {photoPaths.Count} fotos...");
                    
                    for (int i = 0; i < photoPaths.Count && i < 3; i++)
                    {
                        if (File.Exists(photoPaths[i]))
                        {
                            // Usar timestamp con milisegundos para evitar conflictos
                            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                            var shotFileName = $"{timestamp}_raw.png";
                            var shotDestPath = Path.Combine(shotsDir, shotFileName);
                            
                            // Si el archivo ya existe, agregar índice
                            if (File.Exists(shotDestPath))
                            {
                                shotFileName = $"{timestamp}_{i + 1}_raw.png";
                                shotDestPath = Path.Combine(shotsDir, shotFileName);
                            }
                            
                            // SIEMPRE crear directorio antes de copiar
                            Directory.CreateDirectory(shotsDir);
                            var dirExistsBefore = Directory.Exists(shotsDir);
                            CrashLogger.Log($"CAP_SAVE_RAW: dirBefore={shotsDir}, dirExists={dirExistsBefore}");
                            
                            if (!dirExistsBefore)
                            {
                                var ex = new DirectoryNotFoundException($"No se pudo crear directorio: {shotsDir}");
                                CrashLogger.Log($"CAP_SAVE_FAIL: {ex.ToString()}", ex);
                                throw ex;
                            }
                            
                            File.Copy(photoPaths[i], shotDestPath, overwrite: true);
                            
                            // Verificar que el archivo se escribió correctamente
                            var fileExistsAfter = File.Exists(shotDestPath);
                            if (fileExistsAfter)
                            {
                                var fileInfo = new FileInfo(shotDestPath);
                                savedPaths.Add(shotDestPath);
                                CrashLogger.Log($"RAW saved: {shotDestPath}");
                                CrashLogger.Log($"CAP_SAVE_RAW: Guardado exitoso - finalPath={shotDestPath}, dirExists={Directory.Exists(shotsDir)}, File.Exists={fileExistsAfter}, size={fileInfo.Length} bytes");
                            }
                            else
                            {
                                var ex = new Exception($"Archivo no existe después de copiar: {shotDestPath}");
                                CrashLogger.Log($"CAP_SAVE_FAIL: {ex.ToString()}", ex);
                                throw ex;
                            }
                            
                            // Pequeño delay para evitar timestamps idénticos
                            System.Threading.Thread.Sleep(10);
                        }
                        else
                        {
                            var ex = new FileNotFoundException($"Foto {i + 1} no existe: {photoPaths[i]}");
                            CrashLogger.Log($"CAP_SAVE_FAIL: {ex.Message}", ex);
                        }
                    }
                    
                    CrashLogger.Log($"SaveShotsAsync: Total fotos guardadas: {savedPaths.Count}");
                    return savedPaths;
                }
                catch (Exception ex)
                {
                    CrashLogger.Log($"CAP_SAVE_FAIL: Error en SaveShotsAsync - {ex.Message}", ex);
                    throw;
                }
            });
        }
        
        /// <summary>
        /// Guarda una foto cruda directamente en el directorio de shots.
        /// </summary>
        public async Task<string> SaveRawPhotoAsync(SkiaSharp.SKBitmap bitmap)
        {
            return await Task.Run(() =>
            {
                try
                {
                    // Usar ruta fija determinística: C:\KCMundial\Photos\Shots\
                    var shotsDir = GetShotsDirectory();
                    
                    // SIEMPRE crear directorio antes de guardar
                    Directory.CreateDirectory(shotsDir);
                    var dirExistsBefore = Directory.Exists(shotsDir);
                    CrashLogger.Log($"CAP_SAVE_RAW: dirBefore={shotsDir}, dirExists={dirExistsBefore}");
                    
                    if (!dirExistsBefore)
                    {
                        var ex = new DirectoryNotFoundException($"No se pudo crear directorio: {shotsDir}");
                        CrashLogger.Log($"CAP_SAVE_FAIL: {ex.ToString()}", ex);
                        throw ex;
                    }
                    
                    var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var fileName = $"{timestamp}_raw.png";
                    var fullPath = Path.Combine(shotsDir, fileName);
                    
                    CrashLogger.Log($"CAP_SAVE_RAW: finalPath={fullPath}, bitmapSize={bitmap?.Width}x{bitmap?.Height}");
                    
                    using (var image = SkiaSharp.SKImage.FromBitmap(bitmap))
                    using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
                    using (var stream = File.Create(fullPath))
                    {
                        data.SaveTo(stream);
                    }
                    
                    // Verificar que el archivo se escribió correctamente
                    var fileExistsAfter = File.Exists(fullPath);
                    if (fileExistsAfter)
                    {
                        var fileInfo = new FileInfo(fullPath);
                        CrashLogger.Log($"RAW saved: {fullPath}");
                        CrashLogger.Log($"CAP_SAVE_RAW: Guardado exitoso - finalPath={fullPath}, dirExists={Directory.Exists(shotsDir)}, File.Exists={fileExistsAfter}, size={fileInfo.Length} bytes");
                    }
                    else
                    {
                        var ex = new Exception($"Archivo no existe después de escribir: {fullPath}");
                        CrashLogger.Log($"CAP_SAVE_FAIL: {ex.ToString()}", ex);
                        throw ex;
                    }
                    
                    return fullPath;
                }
                catch (Exception ex)
                {
                    CrashLogger.Log($"CAP_SAVE_FAIL: Error en SaveRawPhotoAsync - {ex.ToString()}", ex);
                    throw; // NO tragar la excepción
                }
            });
        }

        public async Task SaveMetadataAsync(string sessionFolder, List<string> photoPaths, string stripPath, List<string> savedShots)
        {
            await Task.Run(() =>
            {
                var metadata = new
                {
                    SessionDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Photos = savedShots.Select((path, index) => new 
                    { 
                        Index = index + 1, 
                        FileName = Path.GetFileName(path),
                        Path = path
                    }).ToArray(),
                    StripFileName = Path.GetFileName(stripPath),
                    StripPath = stripPath
                };

                var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                var metaPath = Path.Combine(sessionFolder, "meta.json");
                File.WriteAllText(metaPath, json);
            });
        }
    }
}

