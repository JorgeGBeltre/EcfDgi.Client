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
    public class CredentialStartupValidationTests : IDisposable
    {
        private readonly string _validCertPath = Path.Combine(Path.GetTempPath(), $"cred-cert-{Guid.NewGuid():N}.pfx");
        private const string CertPassword = "TestPass123!";

        public CredentialStartupValidationTests()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=101000001, O=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            File.WriteAllBytes(_validCertPath, cert.Export(X509ContentType.Pfx, CertPassword));
        }

        public void Dispose()
        {
            if (File.Exists(_validCertPath)) File.Delete(_validCertPath);
        }

        private WebApplicationFactory<Program> MakeFactory(
            string environment, string? workerSecretKey, string? jwtSecret)
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.UseSetting("ConnectionStrings:DefaultConnection", "InMemory");
                builder.UseSetting("ConnectionStrings:Redis", "");
                builder.UseSetting("EcfEmisor:Rnc", "101000001");
                builder.UseSetting("EcfClientOptions:RncEmisor", "101000001");
                builder.UseSetting("EcfClientOptions:CertificatePath", _validCertPath);
                builder.UseSetting("EcfClientOptions:CertificatePassword", CertPassword);
                builder.UseSetting("WorkerSecretKey", workerSecretKey);
                builder.UseSetting("WORKER_SECRET_KEY", workerSecretKey);
                builder.UseSetting("JwtSettings:Secret", jwtSecret);

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
        public void MissingWorkerSecretKey_PreventsHostFromStarting_OutsideDevelopment()
        {
            using var factory = MakeFactory("Staging", workerSecretKey: null, jwtSecret: "a-real-jwt-secret-that-is-long-enough-1234567890");

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("WorkerSecretKey", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void DefaultWorkerSecretKey_PreventsHostFromStarting_OutsideDevelopment()
        {
            using var factory = MakeFactory("Staging", workerSecretKey: "WorkerSecretKey", jwtSecret: "a-real-jwt-secret-that-is-long-enough-1234567890");

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("WorkerSecretKey", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MissingJwtSecret_PreventsHostFromStarting_OutsideDevelopment()
        {
            using var factory = MakeFactory("Staging", workerSecretKey: "a-real-rotated-worker-secret-1234567890", jwtSecret: null);

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("JwtSettings", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void KnownDefaultJwtSecret_PreventsHostFromStarting_OutsideDevelopment()
        {
            using var factory = MakeFactory(
                "Staging",
                workerSecretKey: "a-real-rotated-worker-secret-1234567890",
                jwtSecret: "DefaultSecretKeyForTesting_MustBeAtLeast32Bytes!");

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("JwtSettings", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RealCredentials_HostStarts_OutsideDevelopment()
        {
            using var factory = MakeFactory(
                "Staging",
                workerSecretKey: "a-real-rotated-worker-secret-1234567890",
                jwtSecret: "a-real-jwt-secret-that-is-long-enough-1234567890");

            using var client = factory.CreateClient();
        }

        [Fact]
        public void DefaultCredentials_DoNotPreventStartup_InDevelopment()
        {
            using var factory = MakeFactory("Development", workerSecretKey: null, jwtSecret: null);

            using var client = factory.CreateClient();
        }
    }
}
