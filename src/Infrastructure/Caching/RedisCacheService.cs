using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using EcfDgii.Client.Application.Common.Interfaces;

namespace EcfDgii.Client.Infrastructure.Caching
{
    public class RedisCacheService : ICacheService
    {
        private readonly IConnectionMultiplexer? _redis;
        private readonly ILogger<RedisCacheService> _logger;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };

        public RedisCacheService(IConnectionMultiplexer? redis, ILogger<RedisCacheService> logger)
        {
            _redis = redis;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private IDatabase? GetDatabase()
        {
            if (_redis == null || !_redis.IsConnected)
            {
                _logger.LogWarning("Redis connection is not available. Cache operations will be bypassed.");
                return null;
            }
            try
            {
                return _redis.GetDatabase();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve Redis database instance.");
                return null;
            }
        }

        public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            try
            {
                var db = GetDatabase();
                if (db == null) return default;

                RedisValue value = await db.StringGetAsync(key);
                if (value.IsNullOrEmpty)
                {
                    return default;
                }

                string jsonString = value.ToString();
                return JsonSerializer.Deserialize<T>(jsonString, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting cache key '{Key}' from Redis.", key);
                return default;
            }
        }

        public async Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken ct = default)
        {
            try
            {
                var db = GetDatabase();
                if (db == null) return;

                string serialized = JsonSerializer.Serialize(value, JsonOptions);
                await db.StringSetAsync(key, serialized, absoluteExpiration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting cache key '{Key}' in Redis.", key);
            }
        }

        public async Task RemoveAsync(string key, CancellationToken ct = default)
        {
            try
            {
                var db = GetDatabase();
                if (db == null) return;

                await db.KeyDeleteAsync(key);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing cache key '{Key}' from Redis.", key);
            }
        }

        public async Task<bool> AcquireLockAsync(string lockKey, string lockValue, TimeSpan expiration, CancellationToken ct = default)
        {
            try
            {
                var db = GetDatabase();
                if (db == null) return false;

                return await db.LockTakeAsync(lockKey, lockValue, expiration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error acquiring distributed lock for key '{LockKey}'.", lockKey);
                return false;
            }
        }

        public async Task<bool> ReleaseLockAsync(string lockKey, string lockValue, CancellationToken ct = default)
        {
            try
            {
                var db = GetDatabase();
                if (db == null) return false;

                return await db.LockReleaseAsync(lockKey, lockValue);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error releasing distributed lock for key '{LockKey}'.", lockKey);
                return false;
            }
        }
    }
}
