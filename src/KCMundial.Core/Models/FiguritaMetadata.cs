namespace KCMundial.Core.Models;

/// <summary>
/// Optional metadata saved alongside a figurita (figuritas\{id}.json).
/// </summary>
public sealed class FiguritaMetadata
{
    public string Id { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    /// <summary>Figurita o foto grande (fotos viejas: Figurita).</summary>
    public PhotoFormat Format { get; set; } = PhotoFormat.Figurita;
    public string? CameraName { get; set; }
    public FaceBox? FaceBox { get; set; }
    /// <summary>URL pública devuelta por el servidor de hosting (para el QR permanente).</summary>
    public string? PermanentUrl { get; set; }
}

public sealed class FaceBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}
