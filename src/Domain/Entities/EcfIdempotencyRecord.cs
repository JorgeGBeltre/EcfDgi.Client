using System;

namespace EcfDgii.Client.Domain.Entities
{
    public enum IdempotencyStatus
    {
        Processing,
        Completed,
        Failed
    }

    public class EcfIdempotencyRecord
    {
        public string Key { get; set; } = string.Empty; // Format: $"{TenantId}:{IdempotencyKey}" (Independent of WorkerKeyId for safe key rotation)
        public string CreatedByWorkerKeyId { get; set; } = string.Empty; // Audit key identifier
        public string PayloadHash { get; set; } = string.Empty; // SHA-256 Hex of request body
        public IdempotencyStatus Status { get; set; } = IdempotencyStatus.Processing;
        public int StatusCode { get; set; }
        public string ContentType { get; set; } = "application/json";
        public string ResponseBody { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? UpdatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(30); // 30-day tax compliance retention
    }
}
