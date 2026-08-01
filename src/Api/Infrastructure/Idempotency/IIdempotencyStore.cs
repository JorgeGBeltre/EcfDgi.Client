using System.Security.Cryptography;
using System.Text;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EcfDgii.Client.Api.Infrastructure.Idempotency
{
    public class IdempotentResult
    {
        public int StatusCode { get; set; }
        public string ContentType { get; set; } = "application/json";
        public string Body { get; set; } = string.Empty;
    }

    public enum IdempotencyReservationStatus
    {
        Reserved,
        AlreadyCompleted,
        AlreadyProcessing,
        PayloadMismatch
    }

    public class IdempotencyReservationResult
    {
        public IdempotencyReservationStatus Status { get; set; }
        public IdempotentResult? CompletedResult { get; set; }
    }

    public interface IIdempotencyStore
    {
        Task<IdempotencyReservationResult> ReserveOrGetAsync(string key, string payloadHash);
        Task CompleteAsync(string key, IdempotentResult result);
    }

    public class DbIdempotencyStore : IIdempotencyStore
    {
        private readonly IServiceScopeFactory _scopeFactory;

        public DbIdempotencyStore(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public async Task<IdempotencyReservationResult> ReserveOrGetAsync(string key, string payloadHash)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var existing = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Key == key);
            if (existing != null)
            {
                if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.OrdinalIgnoreCase))
                {
                    return new IdempotencyReservationResult { Status = IdempotencyReservationStatus.PayloadMismatch };
                }

                if (existing.Status == IdempotencyStatus.Completed)
                {
                    return new IdempotencyReservationResult
                    {
                        Status = IdempotencyReservationStatus.AlreadyCompleted,
                        CompletedResult = new IdempotentResult
                        {
                            StatusCode = existing.StatusCode,
                            ContentType = existing.ContentType,
                            Body = existing.ResponseBody
                        }
                    };
                }

                return new IdempotencyReservationResult { Status = IdempotencyReservationStatus.AlreadyProcessing };
            }

            var newRecord = new EcfIdempotencyRecord
            {
                Key = key,
                PayloadHash = payloadHash,
                Status = IdempotencyStatus.Processing,
                CreatedAt = DateTimeOffset.UtcNow
            };

            try
            {
                db.IdempotencyRecords.Add(newRecord);
                await db.SaveChangesAsync();
                return new IdempotencyReservationResult { Status = IdempotencyReservationStatus.Reserved };
            }
            catch (DbUpdateException)
            {
                // Unique key violation -> Concurrent request reserved key at same time
                return new IdempotencyReservationResult { Status = IdempotencyReservationStatus.AlreadyProcessing };
            }
        }

        public async Task CompleteAsync(string key, IdempotentResult result)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var existing = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Key == key);
            if (existing != null)
            {
                existing.Status = IdempotencyStatus.Completed;
                existing.StatusCode = result.StatusCode;
                existing.ContentType = result.ContentType;
                existing.ResponseBody = result.Body;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }
    }
}
