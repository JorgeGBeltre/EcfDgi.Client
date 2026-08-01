using System;
using EcfDgii.Client.Domain.Common;

namespace EcfDgii.Client.Domain.Entities
{
    public class EcfDocument : AuditableEntity
    {
        public string ENcf { get; set; } = string.Empty;
        public string RncEmisor { get; set; } = string.Empty;
        public string? RncComprador { get; set; }
        public string TenantId { get; set; } = "default-tenant";
        public string SourceTxnId { get; set; } = string.Empty;
        public string DocumentKind { get; set; } = "Invoice";
        public string? TrackId { get; set; }
        public string State { get; set; } = "Received"; // Received, SequenceAllocated, Signed, SentToDgii, AcceptedByDgii, RejectedByDgii, Uncertain
        public decimal TotalAmount { get; set; }
        public decimal ItbisAmount { get; set; }
        public string? SecurityCode { get; set; }
        public string XmlContent { get; set; } = string.Empty;
        public string? SignedXmlContent { get; set; }
        public string? DgiiResponseXml { get; set; }
        public DateTime? ReceiptDate { get; set; }
    }
}
