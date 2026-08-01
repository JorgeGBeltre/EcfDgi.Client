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
        public string Key { get; set; } = string.Empty; // Format: $"{TenantId}:{KeyId}:{IdempotencyKey}"
        public string PayloadHash { get; set; } = string.Empty; // SHA-256 Hex of request body
        public IdempotencyStatus Status { get; set; } = IdempotencyStatus.Processing;
        public int StatusCode { get; set; }
        public string ContentType { get; set; } = "application/json";
        public string ResponseBody { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? UpdatedAt { get; set; }
    }
}
