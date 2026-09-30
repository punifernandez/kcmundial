namespace KCMundial.App.Services;

/// <summary>
/// Arma una versión chica en BGR del área visible del preview (rotada y recortada como se ve en pantalla),
/// para la detección de caras. Es barato: muestreo directo, sin copias intermedias.
/// </summary>
public static class FrameSampler
{
    public static void SampleVisibleBgr(byte[] bgra, int width, int height, int rotationClockwise, double aspect,
        int targetWidth, ref byte[]? output, out int outW, out int outH)
    {
        var rotation = ((rotationClockwise % 360) + 360) % 360 / 90 * 90;
        var swap = rotation is 90 or 270;
        double rw = swap ? height : width;
        double rh = swap ? width : height;

        // Recorte centrado con la proporción del marco, en el espacio ya rotado.
        var cropW = Math.Min(rw, rh * aspect);
        var cropH = cropW / aspect;
        var x0 = (rw - cropW) / 2;
        var y0 = (rh - cropH) / 2;

        outW = (int)Math.Min(targetWidth, cropW);
        outH = Math.Max(1, (int)Math.Round(outW / aspect));
        var step = cropW / outW;
        var size = outW * outH * 3;
        if (output == null || output.Length != size) output = new byte[size];

        var o = 0;
        for (var y = 0; y < outH; y++)
        {
            var ry = (int)(y0 + (y + 0.5) * step);
            for (var x = 0; x < outW; x++)
            {
                var rx = (int)(x0 + (x + 0.5) * step);
                int sx, sy;
                switch (rotation)
                {
                    case 90: sx = ry; sy = height - 1 - rx; break;
                    case 180: sx = width - 1 - rx; sy = height - 1 - ry; break;
                    case 270: sx = width - 1 - ry; sy = rx; break;
                    default: sx = rx; sy = ry; break;
                }
                var i = (Math.Clamp(sy, 0, height - 1) * width + Math.Clamp(sx, 0, width - 1)) * 4;
                output[o++] = bgra[i];
                output[o++] = bgra[i + 1];
                output[o++] = bgra[i + 2];
            }
        }
    }
}
