using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using EcfDgii.Client.Application.Common.Exceptions;

namespace EcfDgii.Client.Api.Middleware
{
    public class GlobalExceptionMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<GlobalExceptionMiddleware> _logger;

        public GlobalExceptionMiddleware(RequestDelegate next, ILogger<GlobalExceptionMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                await _next(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An unhandled exception occurred: {Message}", ex.Message);
                await HandleExceptionAsync(context, ex);
            }
        }

        private static async Task HandleExceptionAsync(HttpContext context, Exception exception)
        {
            context.Response.ContentType = "application/problem+json";

            var problemDetails = new ProblemDetails
            {
                Instance = context.Request.Path,
                Detail = exception.Message
            };

            switch (exception)
            {
                case ValidationException validationException:
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    problemDetails.Status = StatusCodes.Status400BadRequest;
                    problemDetails.Title = "Validation Error";
                    problemDetails.Type = "https://tools.ietf.org/html/rfc9457#section-6.1";
                    problemDetails.Extensions["errors"] = validationException.Errors;
                    break;

                case UnauthorizedAccessException:
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    problemDetails.Status = StatusCodes.Status401Unauthorized;
                    problemDetails.Title = "Unauthorized";
                    problemDetails.Type = "https://tools.ietf.org/html/rfc9457#section-6.2";
                    break;

                default:
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    problemDetails.Status = StatusCodes.Status500InternalServerError;
                    problemDetails.Title = "Internal Server Error";
                    problemDetails.Type = "https://tools.ietf.org/html/rfc9457#section-6.6";
                    break;
            }

            var result = JsonSerializer.Serialize(problemDetails);
            await context.Response.WriteAsync(result);
        }
    }
}
