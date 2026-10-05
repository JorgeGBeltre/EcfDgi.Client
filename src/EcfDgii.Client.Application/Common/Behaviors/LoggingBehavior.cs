using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Shared.Common;
using MediatR;
using Microsoft.Extensions.Logging;

namespace EcfDgii.Client.Application.Common.Behaviors
{
    public class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

        public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
        {
            _logger = logger;
        }

        public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
        {
            var requestName = typeof(TRequest).Name;

            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["RequestType"] = requestName
            });

            _logger.LogInformation("Starting request {RequestName}", requestName);

            var timer = Stopwatch.StartNew();

            var response = await next();

            timer.Stop();

            // MED-143: Inspect response Result to log failure at Warning/Error instead of byte-identical success
            if (response is Result result && result.IsFailure)
            {
                _logger.LogWarning("Request {RequestName} failed in {ElapsedMilliseconds}ms with error: {Error}, errors: {Errors}",
                    requestName, timer.ElapsedMilliseconds, result.Error, result.Errors);
            }
            else if (timer.ElapsedMilliseconds > 2000)
            {
                _logger.LogWarning("Long running request {RequestName} ({ElapsedMilliseconds}ms)", requestName, timer.ElapsedMilliseconds);
            }
            else
            {
                _logger.LogInformation("Completed request {RequestName} in {ElapsedMilliseconds}ms", requestName, timer.ElapsedMilliseconds);
            }

            return response;
        }
    }
}
