using Microsoft.Extensions.Caching.Memory;

namespace EcfDgii.Client.Api.Infrastructure.Idempotency
{
    public class IdempotentResult
    {
        public int StatusCode { get; set; }
        public string ContentType { get; set; } = "application/json";
        public string Body { get; set; } = string.Empty;
    }

    public interface IIdempotencyStore
    {
        Task<IdempotentResult?> GetAsync(string key);
        Task SetAsync(string key, IdempotentResult result, TimeSpan ttl);
    }

    public class MemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly IMemoryCache _cache;

        public MemoryIdempotencyStore(IMemoryCache cache)
        {
            _cache = cache;
        }

        public Task<IdempotentResult?> GetAsync(string key)
        {
            if (_cache.TryGetValue(key, out IdempotentResult? result))
            {
                return Task.FromResult(result);
            }
            return Task.FromResult<IdempotentResult?>(null);
        }

        public Task SetAsync(string key, IdempotentResult result, TimeSpan ttl)
        {
            _cache.Set(key, result, ttl);
            return Task.CompletedTask;
        }
    }
}
