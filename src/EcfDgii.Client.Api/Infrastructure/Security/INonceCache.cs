using System;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace EcfDgii.Client.Api.Infrastructure.Security
{
    public interface INonceCache
    {
        bool TryAddNonce(string keyId, string nonce, TimeSpan ttl);
    }

    public class MemoryNonceCache : INonceCache
    {
        private readonly IMemoryCache _memoryCache;
        private readonly IConnectionMultiplexer? _redis;
        private readonly object _syncLock = new();

        public MemoryNonceCache(IMemoryCache memoryCache, IConnectionMultiplexer? redis = null)
        {
            _memoryCache = memoryCache;
            _redis = redis;
        }

        public bool TryAddNonce(string keyId, string nonce, TimeSpan ttl)
        {
            if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(nonce))
            {
                return false;
            }

            var cacheKey = $"nonce:{keyId}:{nonce}";

            // 1. If Redis is available, perform atomic SET with NX (When.NotExists) and TTL across instances
            if (_redis != null && _redis.IsConnected)
            {
                try
                {
                    var db = _redis.GetDatabase();
                    bool added = db.StringSet(cacheKey, "1", ttl, When.NotExists);
                    if (!added)
                    {
                        return false; // Nonce already seen -> replay attack
                    }
                    return true;
                }
                catch
                {
                    // Fall back to local synchronized cache on transient Redis failure
                }
            }

            // 2. Thread-safe atomic in-process check-and-set
            lock (_syncLock)
            {
                if (_memoryCache.TryGetValue(cacheKey, out _))
                {
                    return false; // Nonce already seen -> replay attack
                }

                _memoryCache.Set(cacheKey, true, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = ttl,
                    Size = 1
                });
                return true;
            }
        }
    }
}
