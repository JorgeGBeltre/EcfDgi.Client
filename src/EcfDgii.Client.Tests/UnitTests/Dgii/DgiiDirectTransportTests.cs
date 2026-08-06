using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Infrastructure.Dgii;
using Moq;
using Xunit;

namespace EcfDgii.Client.UnitTests.Dgii
{
    public class DgiiDirectTransportTests
    {
        /// <summary>Routes every request to a canned response, capturing the Authorization header of
        /// each request seen so a test can assert which token was actually sent.</summary>
        private sealed class ScriptedHandler : HttpMessageHandler
        {
            private readonly Func<int, HttpResponseMessage> _responseFor;
            public int CallCount { get; private set; }
            public System.Collections.Generic.List<string?> BearerTokensSeen { get; } = new();

            public ScriptedHandler(Func<int, HttpResponseMessage> responseFor)
            {
                _responseFor = responseFor;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                CallCount++;
                BearerTokensSeen.Add(request.Headers.Authorization?.Parameter);
                return Task.FromResult(_responseFor(CallCount));
            }
        }

        private static HttpResponseMessage Unauthorized() => new(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{}")
        };

        private static HttpResponseMessage EstadoOk(string estado) => new(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"codigo\":1,\"estado\":\"{estado}\"}}")
        };

        private static EcfEnvironmentConfig MakeConfig() => new()
        {
            ConsultaEstadoUrl = "https://dgii.example/consultaestado",
            RecepcionUrl = "https://dgii.example/recepcion",
        };

        [Fact]
        public async Task ConsultarEstadoAsync_OnFirstTry200_DoesNotInvalidateOrRetry()
        {
            var handler = new ScriptedHandler(_ => EstadoOk("Aceptado"));
            var tokenManagerMock = new Mock<IEcfTokenManager>();
            tokenManagerMock.Setup(t => t.GetTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("tok-1");
            var transport = new DgiiDirectTransport(new HttpClient(handler), tokenManagerMock.Object, MakeConfig());

            var result = await transport.ConsultarEstadoAsync(new ConsultaEstadoRequest("101889063", "E310000000001"));

            Assert.Equal("Aceptado", result.Estado);
            Assert.Equal(1, handler.CallCount);
            tokenManagerMock.Verify(t => t.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task ConsultarEstadoAsync_OnFirstTry401_InvalidatesToken_AndRetriesOnceWithAFreshOne()
        {
            // The whole point of reactive 401 handling: a token DGII rejects mid-window (early
            // revocation, clock skew) must not permanently break polling for the rest of that token's
            // nominal lifetime — the transport has to notice the 401 and get a fresh token itself.
            var handler = new ScriptedHandler(call => call == 1 ? Unauthorized() : EstadoOk("Rechazado"));
            var tokenManagerMock = new Mock<IEcfTokenManager>();
            tokenManagerMock.SetupSequence(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("stale-token")
                .ReturnsAsync("fresh-token");
            var transport = new DgiiDirectTransport(new HttpClient(handler), tokenManagerMock.Object, MakeConfig());

            var result = await transport.ConsultarEstadoAsync(new ConsultaEstadoRequest("101889063", "E310000000001"));

            Assert.Equal("Rechazado", result.Estado);
            Assert.Equal(2, handler.CallCount);
            Assert.Equal(new[] { "stale-token", "fresh-token" }, handler.BearerTokensSeen);
            tokenManagerMock.Verify(t => t.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task ConsultarEstadoAsync_TwoConsecutive401s_DoesNotRetryForever_GivesUpAfterOneRetry()
        {
            // A second 401 (fresh token also rejected — a real outage, not a stale-token blip) must
            // surface as a failed call, not loop: an unbounded reactive-retry would turn one bad
            // response into an infinite request storm against DGII.
            var handler = new ScriptedHandler(_ => Unauthorized());
            var tokenManagerMock = new Mock<IEcfTokenManager>();
            tokenManagerMock.SetupSequence(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("stale-token")
                .ReturnsAsync("still-bad-token");
            var transport = new DgiiDirectTransport(new HttpClient(handler), tokenManagerMock.Object, MakeConfig());

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                transport.ConsultarEstadoAsync(new ConsultaEstadoRequest("101889063", "E310000000001")));

            Assert.Equal(2, handler.CallCount);
            tokenManagerMock.Verify(t => t.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task SendEcfAsync_OnFirstTry401_InvalidatesToken_AndRetriesOnceWithAFreshOne()
        {
            var handler = new ScriptedHandler(call => call == 1
                ? Unauthorized()
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"trackId\":\"t-1\"}") });
            var tokenManagerMock = new Mock<IEcfTokenManager>();
            tokenManagerMock.SetupSequence(t => t.GetTokenAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync("stale-token")
                .ReturnsAsync("fresh-token");
            var transport = new DgiiDirectTransport(new HttpClient(handler), tokenManagerMock.Object, MakeConfig());

            await transport.SendEcfAsync("<xml/>", "file.xml");

            Assert.Equal(2, handler.CallCount);
            tokenManagerMock.Verify(t => t.InvalidateAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
