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
        public const string KeyIdHeader = "X-Worker-Key-Id";
        public const string TimestampHeader = "X-Request-Timestamp";
        public const string NonceHeader = "X-Request-Nonce";
        public const string SignatureHeader = "X-Request-Signature";

        private const int MaxTimeDriftSeconds = 300; // 5 minute max drift window
        private const long MaxRequestBodySizeBytes = 10 * 1024 * 1024; // 10 MB safe limit

        private readonly IWorkerKeyResolver _keyResolver;
        private readonly INonceCache _nonceCache;

        public WorkerAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IWorkerKeyResolver keyResolver,
            INonceCache nonceCache)
            : base(options, logger, encoder)
        {
            _keyResolver = keyResolver;
            _nonceCache = nonceCache;
        }

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var keyId = Request.Headers[KeyIdHeader].FirstOrDefault();
            var timestamp = Request.Headers[TimestampHeader].FirstOrDefault();
            var nonce = Request.Headers[NonceHeader].FirstOrDefault();
            var signature = Request.Headers[SignatureHeader].FirstOrDefault();

            if (string.IsNullOrWhiteSpace(keyId) ||
                string.IsNullOrWhiteSpace(timestamp) ||
                string.IsNullOrWhiteSpace(nonce) ||
                string.IsNullOrWhiteSpace(signature))
            {
                return AuthenticateResult.Fail("Missing required authentication headers (X-Worker-Key-Id, X-Request-Timestamp, X-Request-Nonce, X-Request-Signature).");
            }

            // 1. Verify Timestamp & Clock Drift
            if (!long.TryParse(timestamp, out var clientTs))
            {
                return AuthenticateResult.Fail("Invalid timestamp format.");
            }

            var serverTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var driftSeconds = Math.Abs(serverTs - clientTs);
            if (driftSeconds > MaxTimeDriftSeconds)
            {
                Logger.LogWarning("Worker authentication failed due to clock drift. KeyId: {KeyId}, ClientTs: {ClientTs}, ServerTs: {ServerTs}, DriftSeconds: {DriftSeconds}",
                    keyId, clientTs, serverTs, driftSeconds);
                return AuthenticateResult.Fail($"Request expired due to clock drift (drift: {driftSeconds}s, max allowed: {MaxTimeDriftSeconds}s). Code: timestamp_drift.");
            }

            // 2. Resolve Worker Key Info
            WorkerKeyInfo? keyInfo;
            try
            {
                keyInfo = await _keyResolver.GetKeyInfoAsync(keyId);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error resolving worker key for KeyId: {KeyId}", keyId);
                return AuthenticateResult.Fail("Error verifying credentials.");
            }

            if (keyInfo == null || !keyInfo.IsActive)
            {
                Logger.LogWarning("Worker authentication failed: Unknown or inactive KeyId: {KeyId}", keyId);
                return AuthenticateResult.Fail("Unknown or inactive worker key ID. Code: unknown_key_id.");
            }

            if (keyInfo.ValidUntil.HasValue && keyInfo.ValidUntil.Value < DateTimeOffset.UtcNow)
            {
                Logger.LogWarning("Worker authentication failed: Expired worker key: {KeyId}", keyId);
                return AuthenticateResult.Fail("Worker key has expired. Code: key_expired.");
            }

            // 3. Verify Anti-Replay Nonce
            if (!_nonceCache.TryAddNonce(keyId, nonce, TimeSpan.FromSeconds(MaxTimeDriftSeconds)))
            {
                Logger.LogWarning("Worker authentication failed: Replayed nonce detected. KeyId: {KeyId}, Nonce: {Nonce}", keyId, nonce);
                return AuthenticateResult.Fail("Replayed nonce detected. Code: nonce_replayed.");
            }

            // 4. Safely Read Body with EnableBuffering & Size Limit
            if (Request.ContentLength.HasValue && Request.ContentLength.Value > MaxRequestBodySizeBytes)
            {
                return AuthenticateResult.Fail($"Request body exceeds max allowed size of {MaxRequestBodySizeBytes} bytes.");
            }

            Request.EnableBuffering();
            string bodyStr = string.Empty;
            if (Request.Body.CanRead)
            {
                using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                bodyStr = await reader.ReadToEndAsync();
                Request.Body.Position = 0; // Rewind for model binder / downstream handlers
            }

            // 5. Construct Canonical String & Verify Signature in Constant Time
            var pathAndQuery = Request.Path.Value + Request.QueryString.Value;
            var canonicalString = CanonicalRequestHelper.BuildCanonicalString(Request.Method, pathAndQuery, timestamp, nonce, bodyStr);
            var computedSignature = CanonicalRequestHelper.ComputeHmacSha256(keyInfo.Secret, canonicalString);

            var computedSigBytes = Encoding.UTF8.GetBytes(computedSignature);
            var incomingSigBytes = Encoding.UTF8.GetBytes(signature);

            if (computedSigBytes.Length != incomingSigBytes.Length ||
                !CryptographicOperations.FixedTimeEquals(computedSigBytes, incomingSigBytes))
            {
                Logger.LogWarning("Worker authentication failed: Bad signature. KeyId: {KeyId}", keyId);
                return AuthenticateResult.Fail("Invalid signature. Code: bad_signature.");
            }

            // 6. Build Authenticated Principal with Scoped Claims
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, keyInfo.KeyId),
                new Claim("client_type", "worker"),
                new Claim("worker_key_id", keyInfo.KeyId),
                new Claim("tenant_id", keyInfo.TenantId),
                new Claim("allowed_rncs", string.Join(',', keyInfo.AllowedRncs))
            };

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
    }
}
