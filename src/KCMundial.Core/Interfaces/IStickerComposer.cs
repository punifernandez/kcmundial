namespace KCMundial.Core.Interfaces;

/// <summary>
/// Composes the final figurita: background + photo in the photo window (scale/crop by face).
/// </summary>
public interface IStickerComposer
{
    /// <summary>
    /// Compose and encode to JPEG.
    /// </summary>
    /// <param name="captureBgr">Raw capture BGR (width x height x 3)</param>
    /// <param name="captureWidth">Capture width</param>
    /// <param name="captureHeight">Capture height</param>
    /// <param name="faceCenterX">Face center X in capture</param>
    /// <param name="faceCenterY">Face center Y in capture</param>
    /// <param name="faceHeightPx">Face height in capture (pixels)</param>
    /// <param name="eyesY">Eyes line Y in capture (for alignment)</param>
    /// <param name="outputWidth">Output width (e.g. 591 or 1182)</param>
    /// <param name="outputHeight">Output height (e.g. 827 or 1654)</param>
    /// <param name="quality">JPEG quality 1-100 (e.g. 92)</param>
    /// <param name="backImagePath">Ruta al marco (ej. Fondo_1.png). Si null, se usa el marco por defecto.</param>
    /// <returns>JPEG bytes</returns>
    byte[] Compose(
        byte[] captureBgr,
        int captureWidth,
        int captureHeight,
        double faceCenterX,
        double faceCenterY,
        double faceHeightPx,
        double eyesY,
        int outputWidth,
        int outputHeight,
        int quality = 92,
        string? backImagePath = null);
}
