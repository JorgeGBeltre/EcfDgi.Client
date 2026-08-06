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
        /// <summary>Returns each queued response in order, then repeats the last one — used for the
        /// semilla GET + validarsemilla POST pair RenewTokenAsync issues on every renewal.</summary>
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
            return new EcfTokenManager(httpClient, signerMock.Object, config, "101889063");
        }

        [Fact]
        public async Task GetTokenAsync_CachesTheToken_SoASecondCallDoesNotRenew()
        {
            var handler = new SequencedHandler(SemillaResponse, () => TokenResponse("tok-1", DateTimeOffset.UtcNow.AddHours(1)));
            var manager = MakeManager(handler);

            var first = await manager.GetTokenAsync();
            var second = await manager.GetTokenAsync();

            Assert.Equal(first, second);
            Assert.Equal(2, handler.CallCount); // one semilla GET + one validarsemilla POST — no second renewal
        }

        [Fact]
        public async Task InvalidateAsync_ThenGetTokenAsync_ForcesARenewal_EvenThoughTheCachedTokenHadNotExpired()
        {
            // This is the whole point of reactive 401 handling: DgiiDirectTransport needs a way to say
            // "the token I was just handed was rejected — throw it away and get a fresh one," independent
            // of whatever the proactive 5-minute-margin expiry check would otherwise decide.
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
            Assert.Equal(4, handler.CallCount); // two full renewal round-trips
        }

        /// <summary>Unlike SequencedHandler above, exhausts exactly the queued responses without
        /// repeating the last one — needed when the test asserts an exact total call count across
        /// two independent renewal round-trips.</summary>
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
    }
}
