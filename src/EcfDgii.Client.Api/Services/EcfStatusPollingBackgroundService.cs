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
    /// <summary>
    /// Runs EcfStatusReconciler on a timer for the lifetime of the app. This is the only thing that
    /// makes the ⑤/⑥ status-polling gap actually run in production — EcfStatusReconciler itself is
    /// deliberately kept as a plain scoped class (DbContext-dependent, one pass per call) so it stays
    /// unit-testable without a real host; this service is the thin, untested wiring around it, same
    /// split as ERPConnector's ProcessingWorker/InvoiceProcessingEngine.
    /// </summary>
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

            // Run one pass immediately on startup (don't wait a full interval before the first
            // check — a restart shouldn't silently pause polling for up to PollingInterval), then on
            // the timer's cadence thereafter.
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
                // Normal shutdown.
            }
        }

        private async Task RunOnePassAsync(CancellationToken ct)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var ecfClient = scope.ServiceProvider.GetRequiredService<IEcfClient>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var reconcilerLogger = scope.ServiceProvider.GetRequiredService<ILogger<EcfStatusReconciler>>();

            var reconciler = new EcfStatusReconciler(db, ecfClient, clock, options, reconcilerLogger);

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
                // One bad pass (e.g. a transient DB error) must not kill the background service for
                // the rest of the process lifetime — log and try again on the next tick.
                logger.LogError(ex, "Fallo en el pase de polling de estado DGII; se reintentará en el próximo ciclo.");
            }
        }
    }
}
