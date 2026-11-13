using System.Collections.Generic;

namespace EcfDgii.Client.Application.Documents.Dto
{
    public class SourceReferenceDto
    {
        public string Provider { get; set; } = "QuickBooksDesktop";
        public string TxnId { get; set; } = string.Empty;
        public string EditSequence { get; set; } = string.Empty;
    }

    public class CanonicalHeaderDto
    {
        public string RncEmisor { get; set; } = string.Empty;
        public string RazonSocialEmisor { get; set; } = string.Empty;
        public string? DireccionEmisor { get; set; }
        public string RncComprador { get; set; } = string.Empty;
        public string? IdentificadorExtranjero { get; set; }
        public string RazonSocialComprador { get; set; } = string.Empty;
        public string? CorreoComprador { get; set; }
        public string FechaEmision { get; set; } = string.Empty;
        [System.Obsolete("Use root-level CanonicalDocumentDto.FechaVencimientoSecuencia instead (LOW-049).")]
        public string? FechaVencimientoSecuencia { get; set; }
    }

    public class CanonicalLineDto
    {
        public int LineNumber { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal Amount { get; set; }
        public string? IndicadorFacturacion { get; set; }
    }

    public class CanonicalTotalsDto
    {
        public decimal MontoSubtotal { get; set; }

        public decimal? MontoGravadoTotal { get; set; }

        public decimal? MontoExento { get; set; }

        public List<CanonicalTaxBucketDto>? TaxBuckets { get; set; }

        public decimal MontoItbis { get; set; }
        public decimal MontoTotal { get; set; }
    }

    public class CanonicalTaxBucketDto
    {
        public int Rate { get; set; }

        public decimal Base { get; set; }

        public decimal Tax { get; set; }
    }

    public class CanonicalReferencesDto
    {
        public string CorrectsTxnId { get; set; } = string.Empty;

        public string CorrectsENcf { get; set; } = string.Empty;

        public int? CodigoModificacion { get; set; }

        public string? RazonModificacion { get; set; }

        public string? FechaNcfModificado { get; set; }

        public int? IndicadorNotaCredito { get; set; }

        public string? RncOtroContribuyente { get; set; }
    }

    public class CanonicalRetentionDto
    {
        public int IndicadorAgenteRetencionoPercepcion { get; set; } = 1;

        public decimal? MontoItbisRetenido { get; set; }

        public decimal? MontoIsrRetenido { get; set; }
    }

    public class CanonicalCertificateDto
    {
        public string? CertificateBase64 { get; set; }
        public string? Password { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string? CertificatePath { get; set; }
    }

    public class CanonicalCurrencyDto
    {
        public string Code { get; set; } = "DOP";
        public decimal ExchangeRate { get; set; } = 1m;
    }

    public class CanonicalDocumentDto
    {
        public string? TenantId { get; set; }
        public string? Environment { get; set; }
        public CanonicalCertificateDto? Certificate { get; set; }
        public string? Ncf { get; set; }
        public SourceReferenceDto SourceReference { get; set; } = new SourceReferenceDto();
        public string DocumentKind { get; set; } = "Invoice";
        public string TipoComprobante { get; set; } = "E31";
        public CanonicalHeaderDto Header { get; set; } = new CanonicalHeaderDto();
        public CanonicalCurrencyDto? Currency { get; set; }
        public List<CanonicalLineDto> Lines { get; set; } = new List<CanonicalLineDto>();
        public CanonicalTotalsDto Totals { get; set; } = new CanonicalTotalsDto();
        public CanonicalReferencesDto References { get; set; } = new CanonicalReferencesDto();

        public CanonicalRetentionDto? Retention { get; set; }

        public string? FechaVencimientoSecuencia { get; set; }
    }
}
