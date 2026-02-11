using System;

namespace KCMundial.Services
{
    /// <summary>
    /// Opciones de configuración para background removal
    /// </summary>
    public class BackgroundRemovalOptions
    {
        /// <summary>
        /// Tamaño máximo del lado para preview (default: 320px)
        /// </summary>
        public int PreviewMaxSide { get; set; } = 320;

        /// <summary>
        /// Tamaño máximo del lado para output final (default: 1080px)
        /// </summary>
        public int OutputMaxSide { get; set; } = 1080;

        /// <summary>
        /// Usar GPU si está disponible (default: true)
        /// </summary>
        public bool UseGpu { get; set; } = true;

        /// <summary>
        /// Threshold de confidence para considerar resultado válido (0..1, default: 0.70)
        /// </summary>
        public float ConfidenceThreshold { get; set; } = 0.70f;

        /// <summary>
        /// Habilitar fallback remoto (Remove.bg) si confidence < threshold (default: false)
        /// </summary>
        public bool EnableRemoteFallback { get; set; } = false;

        /// <summary>
        /// Radio de feather para bordes (px, default: 2.0)
        /// </summary>
        public float FeatherPx { get; set; } = 2.0f;

        /// <summary>
        /// Fuerza de dehalo/spill suppression (0..1, default: 0.15)
        /// </summary>
        public float DehaloStrength { get; set; } = 0.15f;

        /// <summary>
        /// Radio de erosión morfológica (px, default: 1)
        /// </summary>
        public int ErosionRadius { get; set; } = 1;

        /// <summary>
        /// Radio de blur gaussiano para suavizado (px, default: 1.5)
        /// </summary>
        public float BlurRadius { get; set; } = 1.5f;

        /// <summary>
        /// Gamma para corrección de alpha (default: 1.2)
        /// </summary>
        public float Gamma { get; set; } = 1.2f;

        /// <summary>
        /// Threshold para binarización inicial (0..1, default: 0.40)
        /// </summary>
        public float Threshold { get; set; } = 0.40f;
    }
}






