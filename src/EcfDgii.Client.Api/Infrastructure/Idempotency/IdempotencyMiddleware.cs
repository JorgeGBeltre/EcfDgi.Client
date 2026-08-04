using System.Security.Cryptography;
using System.Text;

namespace EcfDgii.Client.Api.Infrastructure.Idempotency
{
    public class IdempotencyMiddleware
    {
        public const string IdempotencyHeader = "X-Idempotency-Key";
        private readonly RequestDelegate _next;

        public IdempotencyMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context, IIdempotencyStore idempotencyStore)
        {
            var idempotencyKey = context.Request.Headers[IdempotencyHeader].FirstOrDefault();

            // Only process idempotency for POST / PUT / PATCH requests with an Idempotency-Key header
            if (string.IsNullOrWhiteSpace(idempotencyKey) ||
                (context.Request.Method != HttpMethods.Post &&
                 context.Request.Method != HttpMethods.Put &&
                 context.Request.Method != HttpMethods.Patch))
            {
                await _next(context);
                return;
            }

            var tenantId = context.User.FindFirst("tenant_id")?.Value ?? "default-tenant";
            var keyId = context.User.FindFirst("worker_key_id")?.Value ?? "default-worker";
            // Key identity is Key = $"{tenantId}:{idempotencyKey}" (Independent of KeyId to safely allow key rotation without duplicating fiscal documents)
            var scopedKey = $"{tenantId}:{idempotencyKey}";

            // Read request body safely to compute SHA-256 payload hash
            context.Request.EnableBuffering();
            string bodyStr = string.Empty;
            if (context.Request.Body.CanRead)
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, true, 1024, leaveOpen: true);
                bodyStr = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0; // Rewind for model binder / controller
            }

            var payloadHash = ComputeSha256Hex(bodyStr);
            var reservation = await idempotencyStore.ReserveOrGetAsync(scopedKey, payloadHash, keyId);

            if (reservation.Status == IdempotencyReservationStatus.AlreadyCompleted && reservation.CompletedResult != null)
            {
                context.Response.StatusCode = reservation.CompletedResult.StatusCode;
                context.Response.ContentType = reservation.CompletedResult.ContentType;
                context.Response.Headers["X-Cache-Lookup"] = "Hit-Idempotent";
                await context.Response.WriteAsync(reservation.CompletedResult.Body, Encoding.UTF8);
                return;
            }

            if (reservation.Status == IdempotencyReservationStatus.AlreadyProcessing)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"Request with this Idempotency-Key is currently being processed.\"}");
                return;
            }

            if (reservation.Status == IdempotencyReservationStatus.PayloadMismatch)
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"Idempotency key payload mismatch. The same key was used with a different request body.\"}");
                return;
            }

            // Status == Reserved: Execute pipeline and capture response
            var originalResponseBodyStream = context.Response.Body;
            using var responseBuffer = new MemoryStream();
            context.Response.Body = responseBuffer;

            try
            {
                await _next(context);

                var sc = context.Response.StatusCode;
                // Selective caching: Cache business domain results (2xx, 400, 404, 422).
                // Exclude auth/pipeline errors (401, 403), transient timeouts/limits (408, 409, 429), and server failures (>= 500)
                var shouldCache = sc >= 200 && sc < 500 &&
                                  sc != StatusCodes.Status401Unauthorized &&
                                  sc != StatusCodes.Status403Forbidden &&
                                  sc != StatusCodes.Status408RequestTimeout &&
                                  sc != StatusCodes.Status409Conflict &&
                                  sc != StatusCodes.Status429TooManyRequests;

                if (shouldCache)
                {
                    responseBuffer.Seek(0, SeekOrigin.Begin);
                    var responseBody = await new StreamReader(responseBuffer).ReadToEndAsync();

                    var resultToCache = new IdempotentResult
                    {
                        StatusCode = context.Response.StatusCode,
                        ContentType = context.Response.ContentType ?? "application/json",
                        Body = responseBody
                    };

                    await idempotencyStore.CompleteAsync(scopedKey, resultToCache);
                }
            }
            finally
            {
                responseBuffer.Seek(0, SeekOrigin.Begin);
                await responseBuffer.CopyToAsync(originalResponseBodyStream);
                context.Response.Body = originalResponseBodyStream;
            }
        }

        private static string ComputeSha256Hex(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty));
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }
    }
}
