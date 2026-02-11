using System.Threading;
using System.Threading.Tasks;

namespace KCMundial.Services
{
    /// <summary>
    /// Pipeline de calidad para foto final: alpha continuo + postprocess completo
    /// Garantiza que NO corre en el hilo UI y NO bloquea preview
    /// </summary>
    public interface IPipelineFinal
    {
        /// <summary>
        /// Procesa un still frame para foto final (alta calidad)
        /// Retorna rutas de archivos PNG con alpha y cutout
        /// SIEMPRE corre en background thread - nunca bloquea el hilo UI
        /// </summary>
        /// <param name="still">Frame capturado para procesar</param>
        /// <param name="outputFolder">Carpeta donde guardar los resultados</param>
        /// <param name="cancellationToken">Token para cancelar el procesamiento</param>
        /// <returns>Tupla con (rutaAlpha, rutaCutout) o (null, null) si falla</returns>
        Task<(string? alphaPath, string? cutoutPath)> ProcessFrameForFinal(
            System.Windows.Media.Imaging.BitmapSource still,
            string outputFolder,
            CancellationToken cancellationToken = default);
    }
}



