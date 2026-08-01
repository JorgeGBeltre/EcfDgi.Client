using System.Text;

namespace EcfDgii.Client.Api.Infrastructure.Idempotency
{
    public class IdempotencyMiddleware
    {
        public const string IdempotencyHeader = "X-Idempotency-Key";
        private readonly RequestDelegate _next;
        private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

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

            var key = $"idempotency:{idempotencyKey}";
            var cachedResult = await idempotencyStore.GetAsync(key);

            if (cachedResult != null)
            {
                context.Response.StatusCode = cachedResult.StatusCode;
                context.Response.ContentType = cachedResult.ContentType;
                context.Response.Headers["X-Cache-Lookup"] = "Hit-Idempotent";
                await context.Response.WriteAsync(cachedResult.Body, Encoding.UTF8);
                return;
            }

            // Capture the response stream to cache the original response
            var originalResponseBodyStream = context.Response.Body;
            using var responseBuffer = new MemoryStream();
            context.Response.Body = responseBuffer;

            await _next(context);

            if (context.Response.StatusCode >= 200 && context.Response.StatusCode < 300)
            {
                responseBuffer.Seek(0, SeekOrigin.Begin);
                var responseBody = await new StreamReader(responseBuffer).ReadToEndAsync();
                responseBuffer.Seek(0, SeekOrigin.Begin);

                var resultToCache = new IdempotentResult
                {
                    StatusCode = context.Response.StatusCode,
                    ContentType = context.Response.ContentType ?? "application/json",
                    Body = responseBody
                };

                await idempotencyStore.SetAsync(key, resultToCache, DefaultTtl);
            }

            await responseBuffer.CopyToAsync(originalResponseBodyStream);
        }
    }
}
