using System;

namespace EcfDgii.Client.Shared.Common
{
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public static class DominicanTimeZone
    {
        private static readonly Lazy<TimeZoneInfo> _zone = new(() =>
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("America/Santo_Domingo");
            }
            catch (TimeZoneNotFoundException)
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById("SA Western Standard Time");
                }
                catch (TimeZoneNotFoundException)
                {
                    return TimeZoneInfo.CreateCustomTimeZone("America/Santo_Domingo", TimeSpan.FromHours(-4), "Dominican Republic Standard Time", "Dominican Republic Standard Time");
                }
            }
        });

        public static TimeZoneInfo Zone => _zone.Value;

        public static DateTime GetDominicanNow(DateTimeOffset utcNow)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow.UtcDateTime, Zone);
        }
    }

    public static class ClockExtensions
    {
        public static DateTime GetDominicanNow(this IClock clock)
        {
            return DominicanTimeZone.GetDominicanNow(clock.UtcNow);
        }
    }
}
