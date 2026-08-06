using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.IntegrationTests
{
    /// <summary>
    /// Without this, EcfXmlSigner silently falls back to a dummy self-signed certificate when
    /// CertificatePath is missing/unloadable (see EcfXmlSigner's constructor) — the app starts
    /// clean, looks healthy, and signs every e-CF with a certificate DGII will reject. The failure
    /// only surfaces as a DGII rejection in production, not as a deploy-time error. This is the
    /// same class of gap EcfEmisorOptions.ValidateOnStart() closed for the RNC, applied to the
    /// certificate. Deliberately skipped in Development, mirroring EcfXmlSigner's own dummy-cert
    /// fallback, which exists precisely so local dev doesn't need a real DGII certificate.
    /// </summary>
    public class CertificateStartupValidationTests : IDisposable
    {
        private readonly string _validCertPath = Path.Combine(Path.GetTempPath(), $"valid-{Guid.NewGuid():N}.pfx");
        private readonly string _expiredCertPath = Path.Combine(Path.GetTempPath(), $"expired-{Guid.NewGuid():N}.pfx");
        private const string CertPassword = "TestPass123!";

        public CertificateStartupValidationTests()
        {
            WriteSelfSignedPfx(_validCertPath, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            WriteSelfSignedPfx(_expiredCertPath, DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(-1));
        }

        public void Dispose()
        {
            if (File.Exists(_validCertPath)) File.Delete(_validCertPath);
            if (File.Exists(_expiredCertPath)) File.Delete(_expiredCertPath);
        }

        private static void WriteSelfSignedPfx(string path, DateTimeOffset notBefore, DateTimeOffset notAfter)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=101889063, O=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(notBefore, notAfter);
            File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, CertPassword));
        }

        private static WebApplicationFactory<Program> MakeFactory(
            string environment, string? certPath, string? certPassword)
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.UseSetting("ConnectionStrings:DefaultConnection", "InMemory");
                builder.UseSetting("ConnectionStrings:Redis", "");
                builder.UseSetting("EcfEmisor:Rnc", "101889063");
                builder.UseSetting("EcfClientOptions:RncEmisor", "101889063");
                builder.UseSetting("EcfClientOptions:CertificatePath", certPath);
                builder.UseSetting("EcfClientOptions:CertificatePassword", certPassword);
                // Not what this test class is about, but non-Development now also fails fast on
                // these — a real value keeps the "successful start" cases isolated to the
                // certificate check they're actually testing.
                builder.UseSetting("WorkerSecretKey", "a-real-rotated-worker-secret-1234567890");
                builder.UseSetting("JwtSettings:Secret", "a-real-jwt-secret-that-is-long-enough-1234567890");

                builder.ConfigureServices(services =>
                {
                    var sdkDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IEcfClient));
                    if (sdkDescriptor != null)
                    {
                        services.Remove(sdkDescriptor);
                    }
                    services.AddSingleton<IEcfClient>(new Mock<IEcfClient>().Object);
                });
            });
        }

        [Fact]
        public void MissingCertificatePath_PreventsHostFromStarting_OutsideDevelopment()
        {
            using var factory = MakeFactory("Staging", certPath: null, certPassword: null);

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("certificate", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NonexistentCertificateFile_PreventsHostFromStarting_OutsideDevelopment()
        {
            using var factory = MakeFactory("Staging", certPath: @"C:\does\not\exist.pfx", certPassword: "whatever");

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("certificate", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void WrongCertificatePassword_PreventsHostFromStarting_OutsideDevelopment()
        {
            using var factory = MakeFactory("Staging", _validCertPath, certPassword: "wrong-password");

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("certificate", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExpiredCertificate_PreventsHostFromStarting_OutsideDevelopment()
        {
            using var factory = MakeFactory("Staging", _expiredCertPath, CertPassword);

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("certificate", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ValidCertificate_HostStarts_OutsideDevelopment()
        {
            using var factory = MakeFactory("Staging", _validCertPath, CertPassword);

            using var client = factory.CreateClient(); // must not throw
        }

        [Fact]
        public void MissingCertificate_DoesNotPreventStartup_InDevelopment()
        {
            // Regression guard: local dev without a real DGII certificate must still work —
            // EcfXmlSigner's dummy-certificate fallback exists specifically for this case.
            using var factory = MakeFactory("Development", certPath: null, certPassword: null);

            using var client = factory.CreateClient(); // must not throw
        }
    }
}
