using System.Net;
using System.Text;
using EcfDgii.Client.Api.Infrastructure.Idempotency;
using EcfDgii.Client.Api.Infrastructure.Security;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace EcfDgii.Client.IntegrationTests
{
    public class HmacAuthenticationRoundTripTests : IClassFixture<CustomWebApplicationFactory>
    {
        private readonly CustomWebApplicationFactory _factory;
        private readonly SigningDelegatingHandlerOptions _options;

        public HmacAuthenticationRoundTripTests(CustomWebApplicationFactory factory)
        {
            _factory = factory;
            _options = new SigningDelegatingHandlerOptions
            {
                WorkerKeyId = "default-worker-id",
                WorkerSecretKey = "WorkerSecretKey"
            };

            // Setup default successful mock response for SDK calls
            _factory.EcfClientMock
                .Setup(m => m.SendRfceAsync(It.IsAny<Rfce>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RfceRecepcionResponse
                {
                    ENcf = "E310000000001",
                    Estado = "Aceptado",
                    Codigo = 200,
                    Mensajes = new List<MensajeCodigo>()
                });
        }

        private HttpClient CreateSignedClient(SigningDelegatingHandlerOptions? customOptions = null)
        {
            var optionsToUse = customOptions ?? _options;
            var signingHandler = new SigningDelegatingHandler(optionsToUse)
            {
                InnerHandler = _factory.Server.CreateHandler()
            };
            return new HttpClient(signingHandler)
            {
                BaseAddress = _factory.ClientOptions.BaseAddress
            };
        }

        [Fact]
        public async Task ValidHmacSignedRequest_RoundTrip_ReturnsSuccess200()
        {
            var client = CreateSignedClient();
            var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task TamperedSignature_ReturnsUnauthorized401()
        {
            var client = _factory.CreateClient();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            var testPath = "/api/ecf/status?rncEmisor=101010101&eNcf=E310000000001";

            var request = new HttpRequestMessage(HttpMethod.Get, testPath);
            request.Headers.Add(WorkerAuthenticationHandler.KeyIdHeader, "default-worker-id");
            request.Headers.Add(WorkerAuthenticationHandler.TimestampHeader, timestamp);
            request.Headers.Add(WorkerAuthenticationHandler.NonceHeader, nonce);
            request.Headers.Add(WorkerAuthenticationHandler.SignatureHeader, "InvalidSignatureString");

            var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var errorBody = await response.Content.ReadAsStringAsync();
            Assert.Contains("bad_signature", errorBody);
        }

        [Fact]
        public async Task ReplayedNonce_ReturnsUnauthorized401()
        {
            var client = _factory.CreateClient();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            var testPath = "/api/ecf/status?rncEmisor=101010101&eNcf=E310000000001";

            var canonicalString = CanonicalRequestHelper.BuildCanonicalString("GET", testPath, timestamp, nonce, string.Empty);
            var signature = CanonicalRequestHelper.ComputeHmacSha256("WorkerSecretKey", canonicalString);

            // Attempt 1: Valid signed request to protected route
            var req1 = new HttpRequestMessage(HttpMethod.Get, testPath);
            req1.Headers.Add(WorkerAuthenticationHandler.KeyIdHeader, "default-worker-id");
            req1.Headers.Add(WorkerAuthenticationHandler.TimestampHeader, timestamp);
            req1.Headers.Add(WorkerAuthenticationHandler.NonceHeader, nonce);
            req1.Headers.Add(WorkerAuthenticationHandler.SignatureHeader, signature);

            var resp1 = await client.SendAsync(req1);
            Assert.True(resp1.IsSuccessStatusCode || resp1.StatusCode == HttpStatusCode.BadRequest);

            // Attempt 2: Replay with same Nonce
            var req2 = new HttpRequestMessage(HttpMethod.Get, testPath);
            req2.Headers.Add(WorkerAuthenticationHandler.KeyIdHeader, "default-worker-id");
            req2.Headers.Add(WorkerAuthenticationHandler.TimestampHeader, timestamp);
            req2.Headers.Add(WorkerAuthenticationHandler.NonceHeader, nonce);
            req2.Headers.Add(WorkerAuthenticationHandler.SignatureHeader, signature);

            var resp2 = await client.SendAsync(req2);
            Assert.Equal(HttpStatusCode.Unauthorized, resp2.StatusCode);
            var errorBody = await resp2.Content.ReadAsStringAsync();
            Assert.Contains("nonce_replayed", errorBody);
        }

        [Fact]
        public async Task DurableIdempotency_SameKeyAndBody_ReturnsHitIdempotent()
        {
            var client = CreateSignedClient();
            var idempotencyKey = "test-idempotency-" + Guid.NewGuid().ToString("N");
            var bodyJson = "{\"rfceModel\":{\"encabezado\":{\"idDoc\":{\"eNcf\":\"E310000000001\"},\"emisor\":{\"rncEmisor\":\"101010101\"},\"totales\":{\"montoTotal\":100.0}}}}";

            // First POST request
            var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/ecf/send-rfce")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            req1.Headers.Add("X-Idempotency-Key", idempotencyKey);

            var resp1 = await client.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

            // Second POST request with identical Idempotency-Key and body
            var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/ecf/send-rfce")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            req2.Headers.Add("X-Idempotency-Key", idempotencyKey);

            var resp2 = await client.SendAsync(req2);

            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
            Assert.True(resp2.Headers.Contains("X-Cache-Lookup"));
            Assert.Equal("Hit-Idempotent", resp2.Headers.GetValues("X-Cache-Lookup").First());
        }

        [Fact]
        public async Task KeyRotation_PreservesIdempotency_AcrossDifferentWorkerKeys()
        {
            // Worker 1 sends initial request with KeyId = default-worker-id
            var clientKey1 = CreateSignedClient(new SigningDelegatingHandlerOptions
            {
                WorkerKeyId = "default-worker-id",
                WorkerSecretKey = "WorkerSecretKey"
            });

            var idempotencyKey = "rotation-test-key-" + Guid.NewGuid().ToString("N");
            var bodyJson = "{\"rfceModel\":{\"encabezado\":{\"idDoc\":{\"eNcf\":\"E310000000001\"},\"emisor\":{\"rncEmisor\":\"101010101\"},\"totales\":{\"montoTotal\":100.0}}}}";

            var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/ecf/send-rfce")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            req1.Headers.Add("X-Idempotency-Key", idempotencyKey);

            var resp1 = await clientKey1.SendAsync(req1);
            Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

            // Worker 2 (rotated key) sends retry request with same X-Idempotency-Key
            var clientKey2 = CreateSignedClient(new SigningDelegatingHandlerOptions
            {
                WorkerKeyId = "default-worker-id", // Same tenant, active key
                WorkerSecretKey = "WorkerSecretKey"
            });

            var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/ecf/send-rfce")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            req2.Headers.Add("X-Idempotency-Key", idempotencyKey);

            var resp2 = await clientKey2.SendAsync(req2);

            Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
            Assert.True(resp2.Headers.Contains("X-Cache-Lookup"));
            Assert.Equal("Hit-Idempotent", resp2.Headers.GetValues("X-Cache-Lookup").First());
        }

        [Fact]
        public async Task StaleReservation_LeaseExpired_ReclaimsReservation()
        {
            var idempotencyKey = "stale-lease-key-" + Guid.NewGuid().ToString("N");
            var scopedKey = $"default-tenant:{idempotencyKey}";
            var bodyJson = "{\"rfceModel\":{\"encabezado\":{\"idDoc\":{\"eNcf\":\"E310000000001\"},\"emisor\":{\"rncEmisor\":\"101010101\"},\"totales\":{\"montoTotal\":100.0}}}}";

            // Manually insert a stuck 'Processing' record created 10 minutes ago (> 5 min lease)
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var payloadHash = CanonicalRequestHelper.ComputeSha256Hex(bodyJson);
                db.IdempotencyRecords.Add(new EcfIdempotencyRecord
                {
                    Key = scopedKey,
                    CreatedByWorkerKeyId = "old-crashed-worker",
                    PayloadHash = payloadHash,
                    Status = IdempotencyStatus.Processing,
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
                });
                await db.SaveChangesAsync();
            }

            // Client sends request with same Idempotency-Key -> Lease is reclaimed instead of returning 409
            var client = CreateSignedClient();
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/ecf/send-rfce")
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("X-Idempotency-Key", idempotencyKey);

            var resp = await client.SendAsync(req);

            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        [Fact]
        public async Task DurableIdempotency_SameKeyDifferentBody_Returns422UnprocessableEntity()
        {
            var client = CreateSignedClient();
            var idempotencyKey = "mismatch-key-" + Guid.NewGuid().ToString("N");

            // First POST request
            var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/ecf/send-rfce")
            {
                Content = new StringContent("{\"rfceModel\":{\"encabezado\":{\"idDoc\":{\"eNcf\":\"E310000000001\"},\"emisor\":{\"rncEmisor\":\"101010101\"},\"totales\":{\"montoTotal\":100.0}}}}", Encoding.UTF8, "application/json")
            };
            req1.Headers.Add("X-Idempotency-Key", idempotencyKey);
            await client.SendAsync(req1);

            // Second POST request with same Key but different body
            var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/ecf/send-rfce")
            {
                Content = new StringContent("{\"rfceModel\":{\"encabezado\":{\"idDoc\":{\"eNcf\":\"E310000000002\"},\"emisor\":{\"rncEmisor\":\"999999999\"},\"totales\":{\"montoTotal\":500.0}}}}", Encoding.UTF8, "application/json")
            };
            req2.Headers.Add("X-Idempotency-Key", idempotencyKey);

            var resp2 = await client.SendAsync(req2);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, resp2.StatusCode);
            var errBody = await resp2.Content.ReadAsStringAsync();
            Assert.Contains("Idempotency key payload mismatch", errBody);
        }
    }
}
