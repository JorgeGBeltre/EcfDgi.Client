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

        public string GetNextEncfFormatted()
        {
            return $"{Prefix}{(SecuenciaActual + 1):D10}";
        }
    }
}
