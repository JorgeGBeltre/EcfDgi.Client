using System;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Shared.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EcfDgii.Client.Api.Services
{
    public sealed class EcfStatusPollingBackgroundService(
        IServiceScopeFactory scopeFactory,
        EcfStatusPollingOptions options,
        ILogger<EcfStatusPollingBackgroundService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "Polling de estado DGII iniciado (intervalo {IntervalMinutes}min, ventana máxima {MaxHours}h).",
                options.PollingInterval.TotalMinutes, options.MaxPollingWindow.TotalHours);

            using var timer = new PeriodicTimer(options.PollingInterval);

            await RunOnePassAsync(stoppingToken);

            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    await RunOnePassAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        private async Task RunOnePassAsync(CancellationToken ct)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var ecfClient = scope.ServiceProvider.GetRequiredService<IEcfClient>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var reconcilerLogger = scope.ServiceProvider.GetRequiredService<ILogger<EcfStatusReconciler>>();
            var signerResolver = scope.ServiceProvider.GetService<ITenantSignerResolver>();

            var reconciler = new EcfStatusReconciler(db, ecfClient, clock, options, reconcilerLogger, signerResolver);

            try
            {
                var processed = await reconciler.ReconcileAsync(ct);
                if (processed > 0)
                {
                    logger.LogInformation("Polling de estado DGII: {Count} documento(s) procesado(s).", processed);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Fallo en el pase de polling de estado DGII; se reintentará en el próximo ciclo.");
            }
        }
    }
}
