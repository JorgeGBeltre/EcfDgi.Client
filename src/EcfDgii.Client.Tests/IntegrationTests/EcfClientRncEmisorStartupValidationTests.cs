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
    /// EcfClientOptions:RncEmisor is a SECOND, separate identity config key from EcfEmisor:Rnc —
    /// it feeds EcfTokenManager's DGII semilla-signing/token-cache-key path (via EcfClient/
    /// DgiiDirectTransport), not the signed XML's &lt;Emisor&gt; block (that's EcfEmisor:Rnc, already
    /// validated). Discovered while investigating why the committed appsettings.json default RNC
    /// ("101672919") doesn't match the real emisor ("101889063") used everywhere else: BOTH keys had
    /// the wrong committed value, and only EcfEmisor:Rnc had a startup check — EcfClientOptions:RncEmisor
    /// had none, so it could silently be empty or wrong in any environment. Same fail-fast pattern as
    /// the other identity/credential checks in Program.cs.
    /// </summary>
    public class EcfClientRncEmisorStartupValidationTests : IDisposable
    {
        private readonly string _validCertPath = Path.Combine(Path.GetTempPath(), $"rnc-emisor-cert-{Guid.NewGuid():N}.pfx");
        private const string CertPassword = "TestPass123!";

        public EcfClientRncEmisorStartupValidationTests()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=101889063, O=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            File.WriteAllBytes(_validCertPath, cert.Export(X509ContentType.Pfx, CertPassword));
        }

        public void Dispose()
        {
            if (File.Exists(_validCertPath)) File.Delete(_validCertPath);
        }

        private WebApplicationFactory<Program> MakeFactory(string? rncEmisor)
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Staging");
                builder.UseSetting("ConnectionStrings:DefaultConnection", "InMemory");
                builder.UseSetting("ConnectionStrings:Redis", "");
                builder.UseSetting("EcfEmisor:Rnc", "101889063");
                builder.UseSetting("EcfEmisor:RazonSocial", "WILLY CHIC DOMINICANA SRL");
                builder.UseSetting("EcfClientOptions:CertificatePath", _validCertPath);
                builder.UseSetting("EcfClientOptions:CertificatePassword", CertPassword);
                builder.UseSetting("EcfClientOptions:RncEmisor", rncEmisor);
                builder.UseSetting("WorkerSecretKey", "a-real-rotated-worker-secret-1234567890");
                builder.UseSetting("WORKER_SECRET_KEY", "a-real-rotated-worker-secret-1234567890");
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
        public void MissingEcfClientRncEmisor_PreventsHostFromStarting_OutsideDevelopment()
        {
            using var factory = MakeFactory(rncEmisor: null);

            var ex = Record.Exception(() => factory.CreateClient());

            Assert.NotNull(ex);
            Assert.Contains("EcfClientOptions", ex!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void RealEcfClientRncEmisor_HostStarts_OutsideDevelopment()
        {
            using var factory = MakeFactory(rncEmisor: "101889063");

            using var client = factory.CreateClient(); // must not throw
        }
    }
}
