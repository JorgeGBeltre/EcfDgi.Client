using System;

namespace EcfDgii.Client.Api.Infrastructure.Security
{
    /// <summary>
    /// Renewing a DGII signing certificate with a certificate authority takes days, not minutes.
    /// Finding out on expiry day means stopping invoicing while renewal is in progress — this exists
    /// so the warning arrives while there's still time to act on it.
    /// </summary>
    public static class CertificateExpiryPolicy
    {
        public static readonly TimeSpan WarningThreshold = TimeSpan.FromDays(30);
        public static readonly TimeSpan CriticalThreshold = TimeSpan.FromDays(7);

        public enum ExpiryUrgency { Ok, Warning, Critical }

        public static ExpiryUrgency Classify(DateTime now, DateTime notAfter)
        {
            var remaining = notAfter - now;
            if (remaining <= CriticalThreshold) return ExpiryUrgency.Critical;
            if (remaining <= WarningThreshold) return ExpiryUrgency.Warning;
            return ExpiryUrgency.Ok;
        }
    }
}
