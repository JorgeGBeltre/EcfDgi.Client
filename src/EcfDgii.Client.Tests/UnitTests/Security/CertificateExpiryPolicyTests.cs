using System;
using EcfDgii.Client.Api.Infrastructure.Security;
using Xunit;

namespace EcfDgii.Client.UnitTests.Security
{
    public class CertificateExpiryPolicyTests
    {
        private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Classify_WellWithinValidity_ReturnsOk()
        {
            var notAfter = Now.AddDays(60);

            Assert.Equal(CertificateExpiryPolicy.ExpiryUrgency.Ok, CertificateExpiryPolicy.Classify(Now, notAfter));
        }

        [Fact]
        public void Classify_JustOverThirtyDays_ReturnsOk()
        {
            var notAfter = Now.AddDays(31);

            Assert.Equal(CertificateExpiryPolicy.ExpiryUrgency.Ok, CertificateExpiryPolicy.Classify(Now, notAfter));
        }

        [Fact]
        public void Classify_UnderThirtyDaysButOverSeven_ReturnsWarning()
        {
            var notAfter = Now.AddDays(15);

            Assert.Equal(CertificateExpiryPolicy.ExpiryUrgency.Warning, CertificateExpiryPolicy.Classify(Now, notAfter));
        }

        [Fact]
        public void Classify_ExactlyThirtyDays_ReturnsWarning()
        {
            var notAfter = Now.AddDays(30);

            Assert.Equal(CertificateExpiryPolicy.ExpiryUrgency.Warning, CertificateExpiryPolicy.Classify(Now, notAfter));
        }

        [Fact]
        public void Classify_UnderSevenDays_ReturnsCritical()
        {
            var notAfter = Now.AddDays(3);

            Assert.Equal(CertificateExpiryPolicy.ExpiryUrgency.Critical, CertificateExpiryPolicy.Classify(Now, notAfter));
        }

        [Fact]
        public void Classify_ExactlySevenDays_ReturnsCritical()
        {
            var notAfter = Now.AddDays(7);

            Assert.Equal(CertificateExpiryPolicy.ExpiryUrgency.Critical, CertificateExpiryPolicy.Classify(Now, notAfter));
        }

        [Fact]
        public void Classify_AlreadyExpired_StillReturnsCritical_NotSomeOtherValue()
        {
            // Startup already throws before classification is reached for a truly expired cert
            // (see Program.cs's NotAfter check), but the classifier itself should be robust —
            // never silently downgrade a negative remaining-time to Warning/Ok.
            var notAfter = Now.AddDays(-1);

            Assert.Equal(CertificateExpiryPolicy.ExpiryUrgency.Critical, CertificateExpiryPolicy.Classify(Now, notAfter));
        }
    }
}
