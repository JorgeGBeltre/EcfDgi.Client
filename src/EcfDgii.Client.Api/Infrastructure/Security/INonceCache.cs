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

            if (_redis != null && _redis.IsConnected)
            {
                try
                {
                    var db = _redis.GetDatabase();
                    bool added = db.StringSet(cacheKey, "1", ttl, When.NotExists);
                    if (!added)
                    {
                        return false;
                    }
                    return true;
                }
                catch
                {
                }
            }

            lock (_syncLock)
            {
                if (_memoryCache.TryGetValue(cacheKey, out _))
                {
                    return false;
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
