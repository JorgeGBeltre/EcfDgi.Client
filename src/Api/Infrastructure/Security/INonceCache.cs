using Microsoft.Extensions.Caching.Memory;

namespace EcfDgii.Client.Api.Infrastructure.Security
{
    public interface INonceCache
    {
        bool TryAddNonce(string keyId, string nonce, TimeSpan ttl);
    }

    public class MemoryNonceCache : INonceCache
    {
        private readonly IMemoryCache _memoryCache;

        public MemoryNonceCache(IMemoryCache memoryCache)
        {
            _memoryCache = memoryCache;
        }

        public bool TryAddNonce(string keyId, string nonce, TimeSpan ttl)
        {
            if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(nonce))
            {
                return false;
            }

            var cacheKey = $"nonce:{keyId}:{nonce}";
            if (_memoryCache.TryGetValue(cacheKey, out _))
            {
                return false; // Nonce already seen -> replay attack
            }

            _memoryCache.Set(cacheKey, true, ttl);
            return true;
        }
    }
}
