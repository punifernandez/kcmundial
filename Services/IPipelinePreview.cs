using System.Windows.Media.Imaging;

namespace KCMundial.Services
{
    /// <summary>
    /// Pipeline rápido para preview: baja resolución + postprocess mínimo
    /// Garantiza procesamiento rápido sin bloquear el hilo UI
    /// </summary>
    public interface IPipelinePreview
    {
        /// <summary>
        /// Procesa un frame para preview rápido
        /// Retorna bitmap preview procesado o null si falla
        /// NO bloquea el hilo UI - debe ejecutarse en background thread
        /// </summary>
        BitmapSource? ProcessFrameForPreview(BitmapSource frame);
    }
}



