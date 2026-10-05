using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Shared.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using EcfDgii.Client.Infrastructure;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Common;

namespace EcfDgii.Client.Api.Services
{
    public sealed record EcfStatusPollingOptions(
        TimeSpan PollingInterval,
        TimeSpan MinDocumentAge,
        TimeSpan MaxPollingWindow,
        int BatchSize = 50)
    {
        /// <summary>
        /// MaxPollingWindow defaults to 72 hours — the only concrete DGII regulatory timeframe found
        /// in the DGII_md reference docs (the contingency-mode remittance deadline, "Informe Técnico
        /// e-CF v1.0"). DGII's own documentation for the ConsultaEstado service itself does not state
        /// an explicit deadline for how long an e-CF can remain unconfirmed before it should be
        /// escalated — this value is a reasonable, conservative stand-in, NOT a confirmed figure for
        /// this specific scenario. Flagged for the business owner to confirm/override via
        /// EcfStatusPolling:MaxPollingWindowHours in configuration before relying on it operationally.
        /// </summary>
        public static EcfStatusPollingOptions Default => new(
            PollingInterval: TimeSpan.FromMinutes(15),
            MinDocumentAge: TimeSpan.FromMinutes(2),
            MaxPollingWindow: TimeSpan.FromHours(72),
            BatchSize: 50);
    }

    /// <summary>
    /// Closes the ⑤/⑥ gap: prior to this, "SentToDgii" was fully terminal — nothing ever polled DGII
    /// for what happened to a document afterward. Per DGII's own documented ConsultaEstado vocabulary
    /// (DGII_md/"Descripcion Tecnica Servicios DGII.md"), an e-CF DGII initially accepts on receipt
    /// CAN later be rejected on verification ("Rechazado") — without this reconciler, that rejection
    /// would never be observed and the document would sit marked Sent, with fiscal consequence,
    /// indefinitely. One reconciliation pass:
    ///  - Skips documents younger than MinDocumentAge (DGII's own status query can lag behind actual
    ///    receipt — same rationale as DocumentsController.MinimumUncertainAgeBeforeReconciliation).
    ///  - Skips documents polled more recently than PollingInterval.
    ///  - Escalates to RequiresManualReview (without spending another DGII call) once a document has
    ///    been unconfirmed longer than MaxPollingWindow.
    ///  - Otherwise queries DGII and maps the result: Aceptado/Aceptado condicional → AcceptedByDgii,
    ///    Rechazado → RejectedByDgii (logged critical — this is the consequential case), anything else
    ///    (No encontrado, En proceso, an unrecognized value) leaves the document SentToDgii to be
    ///    retried next pass.
    /// A transport failure calling DGII (exception) is treated like an inconclusive answer: attempt
    /// counters advance so the MaxPollingWindow clock still runs, but the document's State is left
    /// untouched — an outage must not be mistaken for a DGII verdict.
    /// </summary>
    public sealed class EcfStatusReconciler(
        ApplicationDbContext db,
        IEcfClient ecfClient,
        IClock clock,
        EcfStatusPollingOptions options,
        ILogger<EcfStatusReconciler> logger,
        ITenantSignerResolver? signerResolver = null)
    {
        private static readonly HashSet<string> AcceptedEstados = new(StringComparer.OrdinalIgnoreCase)
        {
            "Aceptado", "Aceptado condicional",
        };

        private static readonly HashSet<string> RejectedEstados = new(StringComparer.OrdinalIgnoreCase)
        {
            "Rechazado",
        };

        /// <summary>Runs one reconciliation pass. Returns the number of documents actually processed
        /// (escalated or queried) — not the total number of SentToDgii documents in the database.</summary>
        public async Task<int> ReconcileAsync(CancellationToken ct)
        {
            var now = clock.UtcNow.UtcDateTime;
            var minAgeCutoff = now - options.MinDocumentAge;
            var pollDueCutoff = now - options.PollingInterval;
            var batchSize = options.BatchSize > 0 ? options.BatchSize : 50;

            // MED-163: AsNoTracking + Select only the columns needed for reconciliation to avoid materializing unbounded XML columns
            var due = await db.EcfDocuments
                .AsNoTracking()
                .Where(d => (d.State == "Signed" || d.State == "SentToDgii" || d.State == "Uncertain")
                         && ((d.SentToDgiiAt != null && d.SentToDgiiAt <= minAgeCutoff) || (d.SentToDgiiAt == null && d.CreatedAt <= minAgeCutoff))
                         && (d.LastStatusCheckAt == null || d.LastStatusCheckAt <= pollDueCutoff))
                .OrderBy(d => d.SentToDgiiAt ?? d.CreatedAt)
                .Take(Math.Min(batchSize, 200))
                .Select(d => new ReconcileDocDto(
                    d.Id,
                    d.ENcf,
                    d.RncEmisor,
                    d.RncComprador,
                    d.SecurityCode,
                    d.TrackId,
                    d.State,
                    d.Ambiente,
                    d.SentToDgiiAt,
                    d.CreatedAt,
                    d.LastStatusCheckAt,
                    d.StatusCheckAttempts))
                .ToListAsync(ct);

            var processed = 0;
            var dynamicClients = new Dictionary<string, IEcfClient>(StringComparer.OrdinalIgnoreCase);

            foreach (var doc in due)
            {
                processed++;
                var sentTime = doc.SentToDgiiAt ?? doc.CreatedAt;
                var age = now - sentTime;

                if (age >= options.MaxPollingWindow)
                {
                    var isTest = !string.IsNullOrWhiteSpace(doc.Ambiente) &&
                                 EcfEnvironmentHelper.ResolveEcfEnvironment(doc.Ambiente) == EcfEnvironment.Test;
                    var finalState = isTest ? "Expired" : "RequiresManualReview";

                    if (isTest)
                    {
                        logger.LogWarning(
                            "e-CF de prueba {ENcf} (RNC {RncEmisor}, Ambiente {Ambiente}) no se confirmó tras {Hours}h; marcado como Expired.",
                            doc.ENcf, doc.RncEmisor, doc.Ambiente, age.TotalHours);
                    }
                    else
                    {
                        logger.LogCritical(
                            "e-CF {ENcf} (RNC {RncEmisor}) lleva {Hours}h sin confirmación definitiva de DGII " +
                            "(ventana de {MaxHours}h agotada); requiere revisión manual.",
                            doc.ENcf, doc.RncEmisor, age.TotalHours, options.MaxPollingWindow.TotalHours);
                    }

                    await UpdateDocStatusAsync(doc.Id, finalState, now, doc.StatusCheckAttempts, sentTime, null, ct);
                    continue;
                }

                string? dgiiResponseXml = null;
                string? estado = null;
                var attempts = doc.StatusCheckAttempts + 1;
                var newState = doc.State;

                try
                {
                    var clientToUse = ecfClient;
                    if (signerResolver != null && !string.IsNullOrWhiteSpace(doc.RncEmisor))
                    {
                        if (!dynamicClients.TryGetValue(doc.RncEmisor, out clientToUse!))
                        {
                            try
                            {
                                var dynamicSigner = await signerResolver.ResolveSignerAsync(doc.RncEmisor, ct);
                                var env = EcfEnvironmentHelper.ResolveEcfEnvironment(doc.Ambiente);

                                var clientOpts = new EcfClientOptions
                                {
                                    RncEmisor = doc.RncEmisor,
                                    Environment = env,
                                    Mode = IntegrationMode.DgiiDirect,
                                    ValidateSchemasLocal = false
                                };
                                clientToUse = new EcfClient(clientOpts, signer: dynamicSigner);
                                dynamicClients[doc.RncEmisor] = clientToUse;
                            }
                            catch (Exception resolveEx)
                            {
                                logger.LogDebug(resolveEx, "No se pudo resolver signer dinámico para {Rnc}; usando ecfClient default.", doc.RncEmisor);
                                clientToUse = ecfClient;
                            }
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(doc.TrackId))
                    {
                        try
                        {
                            var resultado = await clientToUse.ConsultarResultadoAsync(doc.TrackId, ct);
                            if (resultado != null && !string.IsNullOrWhiteSpace(resultado.Estado))
                            {
                                estado = resultado.Estado.Trim();
                                if (resultado.Mensajes != null && resultado.Mensajes.Count > 0)
                                {
                                    dgiiResponseXml = $"[{resultado.Estado}] " + string.Join("; ", resultado.Mensajes.Select(m => $"[{m.Codigo}] {m.Valor}"));
                                }
                                else
                                {
                                    dgiiResponseXml = $"Estado DGII: {estado}";
                                }
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch
                        {
                            // Fallback to ConsultarEstadoAsync below
                        }
                    }

                    if (string.IsNullOrWhiteSpace(estado) || string.Equals(estado, "No encontrado", StringComparison.OrdinalIgnoreCase))
                    {
                        var response = await clientToUse.ConsultarEstadoAsync(
                            doc.RncEmisor, doc.ENcf, doc.RncComprador, doc.SecurityCode, ct);
                        if (response != null && !string.IsNullOrWhiteSpace(response.Estado))
                        {
                            estado = response.Estado.Trim();
                            dgiiResponseXml = $"Estado DGII: {estado}";
                        }
                    }

                    if (estado != null && AcceptedEstados.Contains(estado))
                    {
                        newState = "AcceptedByDgii";
                        logger.LogInformation("e-CF {ENcf}: DGII confirmó '{Estado}'.", doc.ENcf, estado);
                    }
                    else if (estado != null && RejectedEstados.Contains(estado))
                    {
                        newState = "RejectedByDgii";
                        logger.LogCritical(
                            "ALERTA: e-CF {ENcf} (RNC {RncEmisor}) fue aceptado en recepción y luego " +
                            "RECHAZADO por DGII tras verificación posterior. Requiere atención — el " +
                            "comprobante no tiene validez fiscal.",
                            doc.ENcf, doc.RncEmisor);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    logger.LogInformation("Reconciliación de e-CFs cancelada limpiamente por CancellationToken.");
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Fallo consultando estado DGII para e-CF {ENcf}; se reintentará.", doc.ENcf);
                }

                await UpdateDocStatusAsync(doc.Id, newState, now, attempts, sentTime, dgiiResponseXml, ct);
            }

            return processed;
        }

        private async Task UpdateDocStatusAsync(
            Guid docId,
            string state,
            DateTime lastCheck,
            int attempts,
            DateTime sentTime,
            string? dgiiResponseXml,
            CancellationToken ct)
        {
            try
            {
                if (db.Database.IsRelational())
                {
                    await db.EcfDocuments
                        .Where(d => d.Id == docId)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(d => d.State, state)
                            .SetProperty(d => d.LastStatusCheckAt, lastCheck)
                            .SetProperty(d => d.StatusCheckAttempts, attempts)
                            .SetProperty(d => d.SentToDgiiAt, sentTime)
                            .SetProperty(d => d.DgiiResponseXml, d => dgiiResponseXml ?? d.DgiiResponseXml), ct);
                }
                else
                {
                    var attached = await db.EcfDocuments.FindAsync(new object[] { docId }, ct);
                    if (attached != null)
                    {
                        attached.State = state;
                        attached.LastStatusCheckAt = lastCheck;
                        attached.StatusCheckAttempts = attempts;
                        attached.SentToDgiiAt = sentTime;
                        if (dgiiResponseXml != null)
                        {
                            attached.DgiiResponseXml = dgiiResponseXml;
                        }
                        await db.SaveChangesAsync(ct);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error persistiendo reconciliación para comprobante {DocId}", docId);
            }
        }
    }

    internal sealed record ReconcileDocDto(
        Guid Id,
        string ENcf,
        string RncEmisor,
        string? RncComprador,
        string? SecurityCode,
        string? TrackId,
        string State,
        string? Ambiente,
        DateTime? SentToDgiiAt,
        DateTime CreatedAt,
        DateTime? LastStatusCheckAt,
        int StatusCheckAttempts);
}
