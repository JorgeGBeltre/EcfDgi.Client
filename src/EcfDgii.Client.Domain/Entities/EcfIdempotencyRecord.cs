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
        public string Key { get; set; } = string.Empty;
        public string CreatedByWorkerKeyId { get; set; } = string.Empty;
        public string PayloadHash { get; set; } = string.Empty;
        public IdempotencyStatus Status { get; set; } = IdempotencyStatus.Processing;
        public int StatusCode { get; set; }
        public string ContentType { get; set; } = "application/json";
        public string ResponseBody { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? UpdatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddDays(30);
    }
}
