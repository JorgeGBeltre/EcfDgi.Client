using System;

namespace EcfDgii.Client.Domain.Entities
{
    public class EcfSequence
    {
        public int Id { get; set; }
        public string TenantId { get; set; } = "default-tenant";
        public string TipoComprobante { get; set; } = string.Empty; // e.g. "E31", "E32", "E34", "E41"
        public string Prefix { get; set; } = string.Empty; // e.g. "E31", "E32"
        public long RangoDesde { get; set; } = 1;
        public long RangoHasta { get; set; } = 9999999999;
        public long SecuenciaActual { get; set; } = 0;
        public DateTimeOffset? FechaVencimiento { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Formats the CURRENT SecuenciaActual as an e-NCF — its only caller, EcfSequenceManager,
        /// already increments SecuenciaActual before calling this. Previously added another +1 on
        /// top of that pre-increment, so the very first eNCF issued for every TipoComprobante (every
        /// type, not just tipo 34/41 — this is pre-existing, not specific to this round's changes)
        /// was silently "0000000002", never "0000000001": eNCF #1 in every range went permanently
        /// unused, and every subsequent number was one higher than the actual document count. Found
        /// by EcfSequenceManagerTests exercising the real implementation (every prior test mocked
        /// IEcfSequenceManager, so this never ran for real until then).
        /// </summary>
        public string GetNextEncfFormatted()
        {
            return $"{Prefix}{SecuenciaActual:D10}";
        }
    }
}
