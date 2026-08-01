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

    public class CanonicalReferencesDto
    {
        public string CorrectsTxnId { get; set; } = string.Empty;
        public string CorrectsENcf { get; set; } = string.Empty;
    }

    public class CanonicalDocumentDto
    {
        public SourceReferenceDto SourceReference { get; set; } = new SourceReferenceDto();
        public string DocumentKind { get; set; } = "Invoice"; // Invoice, CreditNote, DebitNote, Bill
        public string TipoComprobante { get; set; } = "E31"; // Default Factura de Crédito Fiscal
        public CanonicalHeaderDto Header { get; set; } = new CanonicalHeaderDto();
        public List<CanonicalLineDto> Lines { get; set; } = new List<CanonicalLineDto>();
        public CanonicalTotalsDto Totals { get; set; } = new CanonicalTotalsDto();
        public CanonicalReferencesDto References { get; set; } = new CanonicalReferencesDto();
    }
}
