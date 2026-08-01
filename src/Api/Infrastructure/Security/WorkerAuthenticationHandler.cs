using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EcfDgii.Client.Api.Infrastructure.Security
{
    public class WorkerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private const string ApiKeyHeader = "X-Worker-API-Key";
        private const string TimestampHeader = "X-Request-Timestamp";
        private const string SignatureHeader = "X-Request-Signature";
        private const int MaxTimeDriftSeconds = 300; // Allow up to 5 min drift to prevent replay attacks

        private readonly IConfiguration _config;

        public WorkerAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IConfiguration config)
            : base(options, logger, encoder)
        {
            _config = config;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var apiKey = Request.Headers[ApiKeyHeader].FirstOrDefault();
            var timestamp = Request.Headers[TimestampHeader].FirstOrDefault();
            var signature = Request.Headers[SignatureHeader].FirstOrDefault();

            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
            {
                return AuthenticateResult.Fail("Missing Authentication Headers");
            }

            // 1. Verify Timestamp (Anti-replay check)
            if (!long.TryParse(timestamp, out var ts) || 
                Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > MaxTimeDriftSeconds)
            {
                return AuthenticateResult.Fail("Request Expired (Timestamp Drift)");
            }

            // 2. Verify HMAC SHA256 Signature
            Request.EnableBuffering();
            var reader = new StreamReader(Request.Body, Encoding.UTF8, true, 1024, true);
            var body = await reader.ReadToEndAsync();
            Request.Body.Position = 0; // Rewind the stream

            var computedSignature = ComputeHmacSha256(apiKey, timestamp + body);
            if (!computedSignature.Equals(signature, StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticateResult.Fail("Invalid Signature");
            }

            // 3. Verify API Key against stored BCrypt hash (or fallback to plain config string)
            var expectedHash = _config["WORKER_API_KEY_HASH"] ?? _config["WorkerApiKeyHash"];
            var expectedPlainKey = _config["WORKER_API_KEY"] ?? _config["WorkerApiKey"];

            bool isKeyValid = false;

            if (!string.IsNullOrEmpty(expectedHash))
            {
                try
                {
                    isKeyValid = BCrypt.Net.BCrypt.Verify(apiKey, expectedHash);
                }
                catch
                {
                    isKeyValid = false;
                }
            }
            else if (!string.IsNullOrEmpty(expectedPlainKey))
            {
                isKeyValid = string.Equals(apiKey, expectedPlainKey, StringComparison.Ordinal);
            }
            else
            {
                // Fallback default for development if no key configured
                isKeyValid = string.Equals(apiKey, "WorkerSecretKey", StringComparison.Ordinal);
            }

            if (!isKeyValid)
            {
                return AuthenticateResult.Fail("Invalid API Key");
            }

            var claims = new[] {
                new Claim(ClaimTypes.NameIdentifier, "worker"),
                new Claim("client_type", "worker")
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }

        private static string ComputeHmacSha256(string key, string data)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash);
        }
    }
}
