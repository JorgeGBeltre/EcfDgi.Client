using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

            var result = await resolver.ResolveSignerAsync("133664692", CancellationToken.None);

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

            var result = await resolver.ResolveSignerAsync("133664692", CancellationToken.None);

            Assert.Same(_defaultSignerMock.Object, result);
        }
    }
}
