using ERPConnector.Ecf;
using ERPConnector.Ecf.Security;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace UnitTests.Security
{
    public class SigningDelegatingHandlerTests
    {
        private class TestInnerHandler : HttpMessageHandler
        {
            public List<HttpRequestMessage> CapturedRequests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Clone headers to capture state at call time
                var clonedRequest = new HttpRequestMessage(request.Method, request.RequestUri);
                foreach (var h in request.Headers)
                {
                    clonedRequest.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                CapturedRequests.Add(clonedRequest);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }
        }

        [Fact]
        public async Task RecalculatesTimestampAndNoncePerAttempt()
        {
            var options = Options.Create(new DgiiOptions
            {
                WorkerKeyId = "worker-key-1",
                WorkerSecretKey = "SuperSecretKey123!"
            });

            var innerHandler = new TestInnerHandler();
            var signingHandler = new SigningDelegatingHandler(options)
            {
                InnerHandler = innerHandler
            };

            var client = new HttpClient(signingHandler);

            // First request attempt
            var req1 = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5091/api/test")
            {
                Content = new StringContent("{\"body\":1}")
            };
            await client.SendAsync(req1);

            // Wait brief moment so unix timestamp or nonce changes
            await Task.Delay(10);

            // Second request attempt (simulating Polly retry)
            var req2 = new HttpRequestMessage(HttpMethod.Post, "http://localhost:5091/api/test")
            {
                Content = new StringContent("{\"body\":1}")
            };
            await client.SendAsync(req2);

            Assert.Equal(2, innerHandler.CapturedRequests.Count);

            var firstAttemptHeaders = innerHandler.CapturedRequests[0].Headers;
            var secondAttemptHeaders = innerHandler.CapturedRequests[1].Headers;

            var nonce1 = firstAttemptHeaders.GetValues("X-Request-Nonce").First();
            var nonce2 = secondAttemptHeaders.GetValues("X-Request-Nonce").First();

            Assert.NotEqual(nonce1, nonce2); // Nonces must be unique per attempt
        }
    }
}
