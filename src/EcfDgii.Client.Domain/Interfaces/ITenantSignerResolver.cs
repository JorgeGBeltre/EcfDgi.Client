using System.Threading;
using System.Threading.Tasks;

namespace EcfDgii.Client.Domain.Interfaces
{
    /// <summary>
    /// Resuelve dinámicamente el firmador XML (IEcfXmlSigner) correspondiente
    /// a un contribuyente / tenant a partir de su RNC o identificador.
    /// </summary>
    public interface ITenantSignerResolver
    {
        /// <summary>
        /// Obtiene la instancia de IEcfXmlSigner configurada con el certificado digital activo
        /// para el RNC especificado (buscando en base de datos de tenants o disco).
        /// Si no se encuentra un certificado específico para el RNC o ante errores temporales,
        /// retorna el firmador por defecto del sistema (fallback seguro).
        /// </summary>
        /// <param name="rnc">RNC o Cédula del contribuyente (con o sin guiones).</param>
        /// <param name="ct">Token de cancelación.</param>
        /// <returns>Instancia de IEcfXmlSigner lista para firmar.</returns>
        Task<IEcfXmlSigner> ResolveSignerAsync(string? rnc, CancellationToken ct = default);
    }
}
