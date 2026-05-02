using System;
using System.Threading;
using System.Threading.Tasks;

using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Exceptions;

namespace EcfDgii.Client.Application.Services
{
    internal static class PollingHelper
    {
        internal static async Task<T> PollUntilCompleteAsync<T>(
            Func<Task<T>> fn,
            Func<T, bool> isComplete,
            PollingOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var opts = options ?? new PollingOptions();
            var delay = opts.InitialDelayMs;
            var retries = 0;
            var startTime = DateTimeOffset.UtcNow;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await fn().ConfigureAwait(false);

                if (isComplete(result))
                    return result;

                retries++;

                if (retries >= opts.MaxRetries)
                    throw new PollingMaxRetriesException(opts.MaxRetries);

                if (opts.TimeoutMs.HasValue)
                {
                    var elapsed = (DateTimeOffset.UtcNow - startTime).TotalMilliseconds;
                    if (elapsed >= opts.TimeoutMs.Value)
                        throw new PollingTimeoutException();
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = (int)Math.Min(delay * opts.BackoffMultiplier, opts.MaxDelayMs);
            }
        }
    }
}
