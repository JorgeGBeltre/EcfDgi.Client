using System;
using System.Threading;
using System.Threading.Tasks;

namespace EcfDgii.Client.Application.Common.Interfaces
{
    public interface ICacheService
    {
        Task<T?> GetAsync<T>(string key, CancellationToken ct = default);
        Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken ct = default);
        Task RemoveAsync(string key, CancellationToken ct = default);
        Task<bool> AcquireLockAsync(string lockKey, string lockValue, TimeSpan expiration, CancellationToken ct = default);
        Task<bool> ReleaseLockAsync(string lockKey, string lockValue, CancellationToken ct = default);
    }
}
