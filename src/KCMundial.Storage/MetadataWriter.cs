using System.Text.Json;
using KCMundial.Core.Interfaces;
using KCMundial.Core.Models;

namespace KCMundial.Storage;

public sealed class MetadataWriter
{
    private readonly IPathResolver _pathResolver;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public MetadataWriter(IPathResolver pathResolver)
    {
        _pathResolver = pathResolver;
    }

    public void Write(FiguritaMetadata metadata)
    {
        var path = Path.Combine(_pathResolver.FiguritasFolder, metadata.Id + ".json");
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        File.WriteAllText(path, json);
    }

    /// <summary>Lee la metadata de una figurita si existe.</summary>
    public FiguritaMetadata? Read(string figuritaId)
    {
        var path = Path.Combine(_pathResolver.FiguritasFolder, figuritaId + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<FiguritaMetadata>(json);
        }
        catch
        {
            return null;
        }
    }
}
