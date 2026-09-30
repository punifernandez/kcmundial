using System.Net.Http;
using System.Text.Json;
using KCMundial.Core.Interfaces;

namespace KCMundial.App.Services;

public sealed class PhotoUploadService : IPhotoUploadService
{
    private const string UploadUrl = "https://kcmundial.puniweb.com/upload";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly IAppLogger? _logger;

    public PhotoUploadService(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<string?> UploadAsync(byte[] jpegBytes, string suggestedFileName, CancellationToken cancellationToken = default)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(jpegBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            content.Add(fileContent, "file", suggestedFileName);

            var response = await HttpClient.PostAsync(UploadUrl, content, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var err = JsonSerializer.Deserialize<JsonElement>(json);
                var msg = err.TryGetProperty("error", out var e) ? e.GetString() : response.ReasonPhrase;
                _logger?.Warn($"Upload failed {response.StatusCode}: {msg}");
                return null;
            }

            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            if (doc.TryGetProperty("url", out var urlNode))
            {
                var url = urlNode.GetString();
                _logger?.Info($"Upload OK: {url}");
                return url;
            }

            _logger?.Warn("Upload response missing 'url'");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error("Upload exception", ex);
            return null;
        }
    }
}
