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
        public string RncComprador { get; set; } = string.Empty;
        public string RazonSocialComprador { get; set; } = string.Empty;
        /// <summary>
        /// ISO 8601 (yyyy-MM-dd) — the canonical, jurisdiction-neutral form callers should send.
        /// DocumentsController.NormalizeFechaDgii converts it into DGII's dd-MM-yyyy before it reaches
        /// the XML; a value already in dd-MM-yyyy is accepted and passed through unchanged.
        /// </summary>
        public string FechaEmision { get; set; } = string.Empty;
    }

    public class CanonicalLineDto
    {
        public int LineNumber { get; set; }
        public string ItemName { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal Amount { get; set; }
    }

    public class CanonicalTotalsDto
    {
        public decimal MontoSubtotal { get; set; }
        public decimal MontoItbis { get; set; }
        public decimal MontoTotal { get; set; }
    }

    /// <summary>
    /// Maps to DGII's "F. Información de Referencia" section — required (obligatoriedad=1) for tipo
    /// 34 (Nota de Crédito), per "Formato Comprobante Fiscal Electrónico (e-CF) V1.0 (1).md" lines
    /// 1105-1136. CorrectsENcf is DGII's NCFModificado: the e-NCF this document affects, which "debe
    /// haber sido remitido previamente a la DGII" — the caller is responsible for only referencing an
    /// e-NCF that's actually already been sent (this DTO doesn't verify that).
    /// </summary>
    public class CanonicalReferencesDto
    {
        public string CorrectsTxnId { get; set; } = string.Empty;

        /// <summary>DGII: NCFModificado. Obligatorio for tipo 34.</summary>
        public string CorrectsENcf { get; set; } = string.Empty;

        /// <summary>
        /// DGII: CodigoModificacion. Obligatorio for tipo 34. 1=Anula el NCF modificado,
        /// 2=Corrige texto, 3=Corrige montos, 4=Reemplazo NCF emitido en contingencia,
        /// 5=Referencia Factura de Consumo Electrónica. Codes 1-3 apply only to notas de crédito/débito.
        /// </summary>
        public int? CodigoModificacion { get; set; }

        /// <summary>DGII: RazonModificacion. Opcional — free-text reason (e.g. "error en precio").</summary>
        public string? RazonModificacion { get; set; }

        /// <summary>
        /// DGII: FechaNCFModificado. Send ISO 8601 (yyyy-MM-dd) like every other date on this DTO —
        /// NormalizeFechaDgii converts it. Per the spec, condicional to the e-CF being a
        /// contingency-paper-sequence replacement — NOT required for a normal electronic-to-electronic
        /// Nota de Crédito. Left null in that (the common) case.
        /// </summary>
        public string? FechaNcfModificado { get; set; }

        /// <summary>
        /// DGII: RNCOtroContribuyente. Condicional — only when the emisor's RNC differs from the
        /// modified document's RNC (dissolution/merger/split). Not applicable to this deployment's
        /// single-emisor design; left null in practice.
        /// </summary>
        public string? RncOtroContribuyente { get; set; }
    }

    /// <summary>
    /// Maps to DGII's "Retención" area — obligatorio (1) ONLY for tipo 41 (Comprobante de Compras)
    /// and 47, per the same spec, line 724: the buyer (Willy Chic, completing the e-CF on behalf of
    /// an informal/non-electronic-invoicing seller) withholds and reports ITBIS/ISR.
    /// </summary>
    public class CanonicalRetentionDto
    {
        /// <summary>DGII: IndicadorAgenteRetencionoPercepcion. 1="R" (retenedor), 2="P" (percepción).</summary>
        public int IndicadorAgenteRetencionoPercepcion { get; set; } = 1;

        /// <summary>DGII: MontoITBISRetenido.</summary>
        public decimal MontoItbisRetenido { get; set; }

        /// <summary>
        /// DGII: MontoISRRetenido. Per the spec, condicional to IndicadorBienoServicio=2 (Servicio) on
        /// the line item(s) — left null when not applicable.
        /// </summary>
        public decimal? MontoIsrRetenido { get; set; }
    }

    public class CanonicalDocumentDto
    {
        public string? Ncf { get; set; }
        public SourceReferenceDto SourceReference { get; set; } = new SourceReferenceDto();
        public string DocumentKind { get; set; } = "Invoice"; // Invoice, CreditNote, DebitNote, Bill
        public string TipoComprobante { get; set; } = "E31"; // Default Factura de Crédito Fiscal
        public CanonicalHeaderDto Header { get; set; } = new CanonicalHeaderDto();
        public List<CanonicalLineDto> Lines { get; set; } = new List<CanonicalLineDto>();
        public CanonicalTotalsDto Totals { get; set; } = new CanonicalTotalsDto();
        public CanonicalReferencesDto References { get; set; } = new CanonicalReferencesDto();

        /// <summary>Required (obligatorio) only for TipoComprobante "E41". Null for every other type.</summary>
        public CanonicalRetentionDto? Retention { get; set; }
    }
}
