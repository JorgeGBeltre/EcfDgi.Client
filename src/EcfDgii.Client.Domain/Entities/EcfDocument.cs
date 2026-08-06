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
        public string? TrackId { get; set; }
        // Signature-validity axis: Unsigned (no real certificate — nothing transmitted) vs Signed
        // (DGII acknowledged receipt with a TrackId). AwaitingTransmission is the transient between
        // local signing and that acknowledgement. "SentToDgii" was the previous name for Signed and
        // may still exist in rows written before the rename.
        // Received, SequenceAllocated, AwaitingTransmission, Signed, AcceptedByDgii, RejectedByDgii, Uncertain, Unsigned,
        // RequiresManualReview (status-polling window exhausted without a definitive DGII answer —
        // see EcfStatusReconciler; distinct from RejectedByDgii, which means DGII DID answer, and no).
        public string State { get; set; } = "Received";
        public decimal TotalAmount { get; set; }
        public decimal ItbisAmount { get; set; }
        public string? SecurityCode { get; set; }
        public string XmlContent { get; set; } = string.Empty;
        public string? SignedXmlContent { get; set; }
        public string? DgiiResponseXml { get; set; }
        public DateTime? ReceiptDate { get; set; }

        // Post-send status polling (EcfStatusReconciler). SentToDgiiAt is stamped once, the first time
        // State becomes "SentToDgii" — deliberately NOT reusing UpdatedAt for this, since UpdatedAt is
        // touched on every subsequent poll too and would make "time since sent" reset on each check.
        public DateTime? SentToDgiiAt { get; set; }
        public DateTime? LastStatusCheckAt { get; set; }
        public int StatusCheckAttempts { get; set; }
    }
}
