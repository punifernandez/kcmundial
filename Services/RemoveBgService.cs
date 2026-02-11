using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;

namespace KCMundial.Services
{
    /// <summary>
    /// Servicio para eliminar fondo usando Remove.bg API (IA profesional)
    /// </summary>
    public class RemoveBgService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private string? _apiKey;
        private const string REMOVEBG_API_URL = "https://api.remove.bg/v1.0/removebg";

        public RemoveBgService()
        {
            _httpClient = new HttpClient();
            LoadApiKey();
        }

        private void LoadApiKey()
        {
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                var apiKeyPath = Path.Combine(desktop, "KCMundial", "removebg_api_key.txt");
                
                if (File.Exists(apiKeyPath))
                {
                    _apiKey = File.ReadAllText(apiKeyPath).Trim();
                    var message = "✓ API Key de Remove.bg cargada";
                    System.Diagnostics.Debug.WriteLine(message);
                    LogService.Write(message);
                }
                else
                {
                    var message = "⚠ No se encontró API Key de Remove.bg. Usando método básico.";
                    System.Diagnostics.Debug.WriteLine(message);
                    LogService.Write(message);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al cargar API Key: {ex.Message}");
            }
        }

        /// <summary>
        /// Elimina el fondo usando Remove.bg API desde ruta de archivo
        /// </summary>
        public async Task<SKBitmap?> RemoveBackgroundAsync(string imagePath)
        {
            if (string.IsNullOrEmpty(_apiKey))
            {
                var message = "⚠ Remove.bg API Key no configurada";
                System.Diagnostics.Debug.WriteLine(message);
                LogService.Write(message);
                return null;
            }

            try
            {
                var message1 = "Eliminando fondo con Remove.bg API...";
                System.Diagnostics.Debug.WriteLine(message1);
                LogService.Write(message1);
                
                // Configurar headers con la API key
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _apiKey);
                
                using (var content = new MultipartFormDataContent())
                {
                    // Agregar imagen
                    var imageBytes = File.ReadAllBytes(imagePath);
                    content.Add(new ByteArrayContent(imageBytes), "image_file", Path.GetFileName(imagePath));
                    
                    // Opciones
                    content.Add(new StringContent("auto"), "size"); // auto detecta el tamaño óptimo
                    content.Add(new StringContent("png"), "format"); // PNG para mantener transparencia

                    // Enviar request
                    var response = await _httpClient.PostAsync(REMOVEBG_API_URL, content);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        var resultBytes = await response.Content.ReadAsByteArrayAsync();
                        
                        // Convertir bytes a SKBitmap
                        using (var stream = new MemoryStream(resultBytes))
                        {
                            var bitmap = SKBitmap.Decode(stream);
                            var message = "✓ Fondo eliminado exitosamente con Remove.bg API";
                            System.Diagnostics.Debug.WriteLine(message);
                            LogService.Write(message);
                            return bitmap;
                        }
                    }
                    else
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        var message = $"✗ Error en Remove.bg API: {response.StatusCode} - {errorContent}";
                        System.Diagnostics.Debug.WriteLine(message);
                        LogService.Write(message);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error al usar Remove.bg API: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Elimina el fondo usando Remove.bg API desde SKBitmap
        /// </summary>
        public async Task<SKBitmap?> RemoveBackgroundAsync(SKBitmap bitmap)
        {
            if (bitmap == null || bitmap.IsNull)
                return null;

            // Guardar temporalmente
            string tempPath = Path.Combine(Path.GetTempPath(), $"removebg_{Guid.NewGuid()}.png");
            try
            {
                using (var image = SKImage.FromBitmap(bitmap))
                {
                    using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
                    {
                        using (var stream = File.Create(tempPath))
                        {
                            data.SaveTo(stream);
                        }
                    }
                }

                return await RemoveBackgroundAsync(tempPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        public bool IsAvailable => !string.IsNullOrEmpty(_apiKey);

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}

