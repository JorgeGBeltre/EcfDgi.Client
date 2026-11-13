using System;
using EcfDgii.Client.Domain.Common;

namespace EcfDgii.Client.Domain.Entities
{
    public class EcfDocument : AuditableEntity
    {
        public string? Ncf { get; set; }
        public string ENcf { get; set; } = string.Empty;
        public string RncEmisor { get; set; } = string.Empty;
        public string? RncComprador { get; set; }
        public string TenantId { get; set; } = "default-tenant";
        public string SourceTxnId { get; set; } = string.Empty;
        public string EditSequence { get; set; } = string.Empty;
        public string DocumentKind { get; set; } = "Invoice";
        public string? Ambiente { get; set; }
        public string? TrackId { get; set; }
        public string State { get; set; } = "Received";
        public decimal TotalAmount { get; set; }
        public decimal ItbisAmount { get; set; }
        public string? SecurityCode { get; set; }
        public string XmlContent { get; set; } = string.Empty;
        public string? SignedXmlContent { get; set; }
        public string? SignedRfceContent { get; set; }
        public string? DgiiResponseXml { get; set; }
        public DateTime? ReceiptDate { get; set; }

        public DateTime? SentToDgiiAt { get; set; }
        public DateTime? LastStatusCheckAt { get; set; }
        public int StatusCheckAttempts { get; set; }
    }
}
