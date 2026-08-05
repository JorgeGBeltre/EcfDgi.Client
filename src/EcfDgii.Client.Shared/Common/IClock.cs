using System;

namespace EcfDgii.Client.Shared.Common
{
    /// <summary>
    /// Abstraction over the current time. Inject this instead of calling DateTime.UtcNow /
    /// DateTimeOffset.UtcNow directly anywhere time-based logic needs to be deterministically
    /// testable (e.g. "has enough time passed since X to trust Y").
    /// </summary>
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
