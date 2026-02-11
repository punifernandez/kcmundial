namespace KCMundial
{
    /// <summary>
    /// Fases de arranque para aislar el crash por eliminación progresiva.
    /// Cada fase habilita un subsistema adicional.
    /// </summary>
    public enum StartupPhase
    {
        /// <summary>
        /// Solo ventana principal vacía. Sin ViewModel, sin cámara, sin timers, sin Skia, sin ONNX.
        /// </summary>
        Phase0_OnlyWindow = 0,

        /// <summary>
        /// Ventana + ViewModel + UI bindings. Sin cámara, sin preview, sin procesamiento.
        /// </summary>
        Phase1_NoCamera = 1,

        /// <summary>
        /// Ventana + ViewModel + MediaCapture inicializado. Sin preview, sin callbacks.
        /// </summary>
        Phase2_CameraNoPreview = 2,

        /// <summary>
        /// Ventana + ViewModel + MediaCapture + Preview RAW. Sin Skia, sin segmentación, sin unsafe.
        /// </summary>
        Phase3_PreviewNoProcessing = 3,

        /// <summary>
        /// Pipeline completo habilitado.
        /// </summary>
        Phase4_FullPipeline = 4
    }

    /// <summary>
    /// Subfases de Phase4 para aislar el crash en el procesamiento.
    /// </summary>
    public enum Phase4SubPhase
    {
        /// <summary>
        /// Solo copiar frame a buffer seguro (sin Skia, sin ONNX).
        /// </summary>
        Phase4A_MarshalOnly = 0,

        /// <summary>
        /// Convertir a SKBitmap (Skia sí, pero NO ONNX).
        /// </summary>
        Phase4B_SkiaConvertOnly = 1,

        /// <summary>
        /// Inferencia ONNX (ONNX sí, pero NO alpha post ni composición).
        /// </summary>
        Phase4C_ONNX_InferenceOnly = 2,

        /// <summary>
        /// Pipeline completo: alpha + post-proceso + composición.
        /// </summary>
        Phase4D_FullAlphaCompose = 3
    }

    /// <summary>
    /// Subfases de captura de foto para aislar el crash en el flujo de captura.
    /// </summary>
    public enum CaptureSubPhase
    {
        /// <summary>
        /// Solo capturar frame STILL y convertirlo a SoftwareBitmap seguro (sin guardar, sin procesamiento).
        /// </summary>
        CAP0_RawStillOnly = 0,

        /// <summary>
        /// Capturar y guardar a archivo (sin procesamiento Skia/ONNX).
        /// </summary>
        CAP1_SaveOnly = 1,

        /// <summary>
        /// Cargar foto guardada a SKBitmap (sin ONNX, sin procesamiento).
        /// </summary>
        CAP2_LoadSkia = 2,

        /// <summary>
        /// Correr inferencia ONNX sobre la foto (sin post-proceso ni composición).
        /// </summary>
        CAP3_ONNXOnly = 3,

        /// <summary>
        /// Pipeline completo: ONNX + post-proceso + composición.
        /// </summary>
        CAP4_FullPipeline = 4
    }
}

