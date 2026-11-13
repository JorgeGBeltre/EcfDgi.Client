using System;

namespace EcfDgii.Client.Api.Infrastructure.Security
{
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
