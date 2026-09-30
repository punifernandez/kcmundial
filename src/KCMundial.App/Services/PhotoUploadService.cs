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

            using var response = await HttpClient.PostAsync(UploadUrl, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            string? url = null;
            string? error = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
                    if (doc.RootElement.TryGetProperty("error", out var e)) error = e.GetString();
                }
            }
            catch (JsonException)
            {
                // El servidor no respondió JSON (página de error, proxy, etc.): se registra tal cual.
                error = body;
            }

            if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(url))
            {
                var snippet = (error ?? body).ReplaceLineEndings(" ");
                if (snippet.Length > 300) snippet = snippet[..300] + "…";
                _logger?.Warn($"Upload failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}, {jpegBytes.Length / 1024} KB, response: {snippet}");
                return null;
            }

            _logger?.Info($"Upload OK ({jpegBytes.Length / 1024} KB): {url}");
            return url;
        }
        catch (Exception ex)
        {
            _logger?.Error("Upload exception", ex);
            return null;
        }
    }
}
