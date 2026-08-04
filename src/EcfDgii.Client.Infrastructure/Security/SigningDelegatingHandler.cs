using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace EcfDgii.Client.Infrastructure.Security
{
    public class SigningDelegatingHandlerOptions
    {
        public string WorkerKeyId { get; set; } = "default-worker-id";
        public string WorkerSecretKey { get; set; } = "WorkerSecretKey";
        public string JwtToken { get; set; } = string.Empty;
    }

    public class SigningDelegatingHandler : DelegatingHandler
    {
        private readonly SigningDelegatingHandlerOptions _options;

        public SigningDelegatingHandler(SigningDelegatingHandlerOptions options)
        {
            _options = options;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var nonce = Guid.NewGuid().ToString("N");
            var keyId = string.IsNullOrWhiteSpace(_options.WorkerKeyId) ? "default-worker-id" : _options.WorkerKeyId;
            var secretKey = string.IsNullOrWhiteSpace(_options.WorkerSecretKey) ? "WorkerSecretKey" : _options.WorkerSecretKey;

            string bodyStr = string.Empty;
            if (request.Content != null)
            {
                await request.Content.LoadIntoBufferAsync();
                bodyStr = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            var method = request.Method.Method.ToUpperInvariant();
            var pathAndQuery = request.RequestUri?.PathAndQuery ?? "/";

            var canonicalString = CanonicalRequestHelper.BuildCanonicalString(method, pathAndQuery, timestamp, nonce, bodyStr);
            var signature = CanonicalRequestHelper.ComputeHmacSha256(secretKey, canonicalString);

            request.Headers.Remove("X-Worker-Key-Id");
            request.Headers.Add("X-Worker-Key-Id", keyId);

            request.Headers.Remove("X-Request-Timestamp");
            request.Headers.Add("X-Request-Timestamp", timestamp);

            request.Headers.Remove("X-Request-Nonce");
            request.Headers.Add("X-Request-Nonce", nonce);

            request.Headers.Remove("X-Request-Signature");
            request.Headers.Add("X-Request-Signature", signature);

            if (!string.IsNullOrWhiteSpace(_options.JwtToken) && !request.Headers.Contains("Authorization"))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.JwtToken);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
