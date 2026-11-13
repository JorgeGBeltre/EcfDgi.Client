using System;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace EcfDgii.Client.UnitTests.Security
{
    public class TenantSignerResolverTests
    {
        private readonly Mock<IEcfXmlSigner> _defaultSignerMock = new();
        private readonly Mock<ILogger<TenantSignerResolver>> _loggerMock = new();

        private TenantSignerResolver CreateResolver(Dictionary<string, string?>? inMemoryConfig = null)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemoryConfig ?? new Dictionary<string, string?>())
                .Build();

            return new TenantSignerResolver(config, _defaultSignerMock.Object, _loggerMock.Object);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("---")]
        public async Task ResolveSignerAsync_EmptyOrInvalidRnc_ReturnsDefaultSigner(string? rnc)
        {
            var resolver = CreateResolver();

            var result = await resolver.ResolveSignerAsync(rnc, CancellationToken.None);

            Assert.Same(_defaultSignerMock.Object, result);
        }

        [Fact]
        public async Task ResolveSignerAsync_NoDatabaseConfigured_ReturnsDefaultSigner()
        {
            var resolver = CreateResolver();

            var result = await resolver.ResolveSignerAsync("130000002", CancellationToken.None);

            Assert.Same(_defaultSignerMock.Object, result);
        }

        [Fact]
        public async Task ResolveSignerAsync_InMemoryDbConnectionString_ReturnsDefaultSignerGracefully()
        {
            var config = new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "InMemory"
            };
            var resolver = CreateResolver(config);

            var result = await resolver.ResolveSignerAsync("130000002", CancellationToken.None);

            Assert.Same(_defaultSignerMock.Object, result);
        }

        [Fact]
        public void DecryptPassword_EncV3WithJwtSettingsSecret_DecryptsCorrectly()
        {
            const string sharedSecret = "UnitTestDummySharedSecretKey_MinimumThirtyTwoBytesLong_0123456789!";
            const string tenantCode = "sede-principal-130000003";
            const string expectedPassword = "TestCertPassword!";

            var key = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                Encoding.UTF8.GetBytes(sharedSecret),
                32,
                info: Encoding.UTF8.GetBytes("ecf:certificate-password:v1"));
            var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
            RandomNumberGenerator.Fill(nonce);
            var plainBytes = Encoding.UTF8.GetBytes(expectedPassword);
            var cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            var aad = Encoding.UTF8.GetBytes($"tenant:{tenantCode.ToLowerInvariant()}|field:CertificatePassword|v3");

            using (var aesGcm = new AesGcm(key, AesGcm.TagByteSizes.MaxSize))
            {
                aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag, aad);
            }

            var combined = new byte[nonce.Length + tag.Length + cipherBytes.Length];
            Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
            Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
            Buffer.BlockCopy(cipherBytes, 0, combined, nonce.Length + tag.Length, cipherBytes.Length);
            var envelope = "enc:v3:" + Convert.ToBase64String(combined);

            var resolver = CreateResolver(new Dictionary<string, string?>
            {
                ["STORAGE_ENCRYPTION_KEY"] = "",
                ["JwtSettings:Secret"] = sharedSecret
            });

            var decryptMethod = typeof(TenantSignerResolver).GetMethod("DecryptPassword", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(decryptMethod);

            var decrypted = (string?)decryptMethod!.Invoke(resolver, new object?[] { envelope, tenantCode });
            Assert.Equal(expectedPassword, decrypted);
        }
    }
}
