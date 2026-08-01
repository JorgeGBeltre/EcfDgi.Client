using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using EcfDgii.Client.Api.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace UnitTests.Security
{
    public class WorkerAuthenticationHandlerTests
    {
        private readonly Mock<IOptionsMonitor<AuthenticationSchemeOptions>> _optionsMock;
        private readonly Mock<ILoggerFactory> _loggerFactoryMock;
        private readonly Mock<UrlEncoder> _encoderMock;
        private readonly Mock<IWorkerKeyResolver> _keyResolverMock;
        private readonly MemoryNonceCache _nonceCache;

        public WorkerAuthenticationHandlerTests()
        {
            _optionsMock = new Mock<IOptionsMonitor<AuthenticationSchemeOptions>>();
            _optionsMock.Setup(o => o.Get(It.IsAny<string>())).Returns(new AuthenticationSchemeOptions());
            _loggerFactoryMock = new Mock<ILoggerFactory>();
            _loggerFactoryMock.Setup(l => l.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);
            _encoderMock = new Mock<UrlEncoder>();

            _keyResolverMock = new Mock<IWorkerKeyResolver>();
            _keyResolverMock.Setup(r => r.GetKeyInfoAsync("worker-key-1"))
                .ReturnsAsync(new WorkerKeyInfo
                {
                    KeyId = "worker-key-1",
                    Secret = "SuperSecretKey123!",
                    TenantId = "tenant-abc",
                    AllowedRncs = new List<string> { "101010101" },
                    IsActive = true
                });

            var memoryCache = new MemoryCache(new MemoryCacheOptions());
            _nonceCache = new MemoryNonceCache(memoryCache);
        }

        [Fact]
        public async Task MissingHeaders_ReturnsFail()
        {
            var context = new DefaultHttpContext();
            var handler = CreateHandler(context);

            var result = await handler.AuthenticateAsync();

            Assert.False(result.Succeeded);
            Assert.Contains("Missing required authentication headers", result.Failure?.Message);
        }

        [Fact]
        public async Task ExpiredTimestamp_ReturnsFailWithDriftCode()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers[WorkerAuthenticationHandler.KeyIdHeader] = "worker-key-1";
            context.Request.Headers[WorkerAuthenticationHandler.TimestampHeader] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 400).ToString(); // 400s drift (> 300s)
            context.Request.Headers[WorkerAuthenticationHandler.NonceHeader] = Guid.NewGuid().ToString("N");
            context.Request.Headers[WorkerAuthenticationHandler.SignatureHeader] = "dummy-signature";

            var handler = CreateHandler(context);
            var result = await handler.AuthenticateAsync();

            Assert.False(result.Succeeded);
            Assert.Contains("timestamp_drift", result.Failure?.Message);
        }

        [Fact]
        public async Task ReplayedNonce_ReturnsFailWithNonceCode()
        {
            var nonce = Guid.NewGuid().ToString("N");
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

            _nonceCache.TryAddNonce("worker-key-1", nonce, TimeSpan.FromMinutes(5)); // Pre-add nonce to trigger replay

            var context = new DefaultHttpContext();
            context.Request.Headers[WorkerAuthenticationHandler.KeyIdHeader] = "worker-key-1";
            context.Request.Headers[WorkerAuthenticationHandler.TimestampHeader] = timestamp;
            context.Request.Headers[WorkerAuthenticationHandler.NonceHeader] = nonce;
            context.Request.Headers[WorkerAuthenticationHandler.SignatureHeader] = "dummy-signature";

            var handler = CreateHandler(context);
            var result = await handler.AuthenticateAsync();

            Assert.False(result.Succeeded);
            Assert.Contains("nonce_replayed", result.Failure?.Message);
        }

        [Fact]
        public async Task ValidCanonicalSignature_ReturnsSuccessWithClaims()
        {
            var keyId = "worker-key-1";
            var secret = "SuperSecretKey123!";
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            var bodyStr = "{\"test\":true}";

            var canonicalString = CanonicalRequestHelper.BuildCanonicalString("POST", "/api/test", timestamp, nonce, bodyStr);
            var signature = CanonicalRequestHelper.ComputeHmacSha256(secret, canonicalString);

            var context = new DefaultHttpContext();
            context.Request.Method = "POST";
            context.Request.Path = "/api/test";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(bodyStr));

            context.Request.Headers[WorkerAuthenticationHandler.KeyIdHeader] = keyId;
            context.Request.Headers[WorkerAuthenticationHandler.TimestampHeader] = timestamp;
            context.Request.Headers[WorkerAuthenticationHandler.NonceHeader] = nonce;
            context.Request.Headers[WorkerAuthenticationHandler.SignatureHeader] = signature;

            var handler = CreateHandler(context);
            var result = await handler.AuthenticateAsync();

            Assert.True(result.Succeeded);
            Assert.NotNull(result.Principal);
            Assert.Equal("worker", result.Principal.FindFirst("client_type")?.Value);
            Assert.Equal("worker-key-1", result.Principal.FindFirst("worker_key_id")?.Value);
            Assert.Equal("tenant-abc", result.Principal.FindFirst("tenant_id")?.Value);
        }

        private WorkerAuthenticationHandler CreateHandler(HttpContext context)
        {
            var handler = new WorkerAuthenticationHandler(
                _optionsMock.Object,
                _loggerFactoryMock.Object,
                _encoderMock.Object,
                _keyResolverMock.Object,
                _nonceCache);

            handler.InitializeAsync(new AuthenticationScheme("WorkerAuth", "WorkerAuth", typeof(WorkerAuthenticationHandler)), context).Wait();
            return handler;
        }
    }
}
