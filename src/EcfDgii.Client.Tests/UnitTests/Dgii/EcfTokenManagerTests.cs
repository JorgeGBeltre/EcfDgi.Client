using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Infrastructure.Dgii;
using EcfDgii.Client.Domain.Interfaces;
using Moq;
using Xunit;

namespace EcfDgii.Client.UnitTests.Dgii
{
    public class EcfTokenManagerTests
    {
        private sealed class SequencedHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpResponseMessage>> _responses;
            public int CallCount { get; private set; }

            public SequencedHandler(params Func<HttpResponseMessage>[] responses)
            {
                _responses = new Queue<Func<HttpResponseMessage>>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                CallCount++;
                var factory = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
                return Task.FromResult(factory());
            }
        }

        private static HttpResponseMessage SemillaResponse() =>
            new(HttpStatusCode.OK) { Content = new StringContent("<semilla><valor>1</valor></semilla>") };

        private static HttpResponseMessage TokenResponse(string token, DateTimeOffset expiry) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"<respuesta><token>{token}</token><expira>{expiry:yyyy-MM-ddTHH:mm:ss.fffZ}</expira></respuesta>")
            };

        private static EcfTokenManager MakeManager(HttpMessageHandler handler)
        {
            var signerMock = new Mock<IEcfXmlSigner>();
            signerMock.Setup(s => s.SignXml(It.IsAny<string>(), It.IsAny<string>())).Returns("<signed/>");
            var httpClient = new HttpClient(handler);
            var config = new EcfEnvironmentConfig { AutenticacionUrl = "https://dgii.example/auth" };
            return new EcfTokenManager(httpClient, signerMock.Object, config, "101000001");
        }

        [Fact]
        public async Task GetTokenAsync_CachesTheToken_SoASecondCallDoesNotRenew()
        {
            var handler = new SequencedHandler(SemillaResponse, () => TokenResponse("tok-1", DateTimeOffset.UtcNow.AddHours(1)));
            var manager = MakeManager(handler);

            var first = await manager.GetTokenAsync();
            var second = await manager.GetTokenAsync();

            Assert.Equal(first, second);
            Assert.Equal(2, handler.CallCount);
        }

        [Fact]
        public async Task InvalidateAsync_ThenGetTokenAsync_ForcesARenewal_EvenThoughTheCachedTokenHadNotExpired()
        {
            var factories = new Func<HttpResponseMessage>[]
            {
                SemillaResponse,
                () => TokenResponse("tok-1", DateTimeOffset.UtcNow.AddHours(1)),
                SemillaResponse,
                () => TokenResponse("tok-2", DateTimeOffset.UtcNow.AddHours(1)),
            };
            var handler = new SequencedHandlerExact(factories);
            var manager = MakeManager(handler);

            var first = await manager.GetTokenAsync();
            await manager.InvalidateAsync();
            var second = await manager.GetTokenAsync();

            Assert.Equal("tok-1", first);
            Assert.Equal("tok-2", second);
            Assert.Equal(4, handler.CallCount);
        }

        private sealed class SequencedHandlerExact : HttpMessageHandler
        {
            private readonly Queue<Func<HttpResponseMessage>> _responses;
            public int CallCount { get; private set; }

            public SequencedHandlerExact(IEnumerable<Func<HttpResponseMessage>> responses)
            {
                _responses = new Queue<Func<HttpResponseMessage>>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                CallCount++;
                return Task.FromResult(_responses.Dequeue()());
            }
        }

        [Fact]
        public async Task GetTokenAsync_TransientSocketResetOnSemilla_RetriesAndSucceeds()
        {
            var factories = new Func<HttpResponseMessage>[]
            {
                () => throw new HttpRequestException("An error occurred while sending the request.", new System.IO.IOException("Connection reset by peer")),
                SemillaResponse,
                () => TokenResponse("tok-recovered", DateTimeOffset.UtcNow.AddHours(1)),
            };
            var handler = new SequencedHandlerExact(factories);
            var manager = MakeManager(handler);

            var token = await manager.GetTokenAsync();

            Assert.Equal("tok-recovered", token);
            Assert.Equal(3, handler.CallCount);
        }
    }
}
