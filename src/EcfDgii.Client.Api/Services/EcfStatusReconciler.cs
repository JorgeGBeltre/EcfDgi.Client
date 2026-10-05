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

            var due = await db.EcfDocuments
                // HIGH-042 & MED-018: Incluir también estado "Uncertain", ordenar por LastStatusCheckAt, y limitar por batchSize
                .Where(d => (d.State == "Signed" || d.State == "SentToDgii" || d.State == "Uncertain")
                         && ((d.SentToDgiiAt != null && d.SentToDgiiAt <= minAgeCutoff) || (d.SentToDgiiAt == null && d.CreatedAt <= minAgeCutoff))
                         && (d.LastStatusCheckAt == null || d.LastStatusCheckAt <= pollDueCutoff))
                .OrderBy(d => d.LastStatusCheckAt ?? DateTime.MinValue)
                .Take(batchSize)
                .ToListAsync(ct);

            var processed = 0;

            foreach (var doc in due)
            {
                processed++;
                var sentTime = doc.SentToDgiiAt ?? doc.CreatedAt;
                if (doc.SentToDgiiAt == null)
                {
                    doc.SentToDgiiAt = sentTime;
                }
                var age = now - sentTime;

                if (age >= options.MaxPollingWindow)
                {
                    doc.LastStatusCheckAt = now;
                    var isTest = !string.IsNullOrWhiteSpace(doc.Ambiente) &&
                                 EcfEnvironmentHelper.ResolveEcfEnvironment(doc.Ambiente) == EcfEnvironment.Test;
                    if (isTest)
                    {
                        // In non-production test environments (PreCertificacion / Test), stale documents have no fiscal consequence;
                        // mark Expired and log at Warning rather than escalating to RequiresManualReview / LogCritical.
                        doc.State = "Expired";
                        logger.LogWarning(
                            "e-CF de prueba {ENcf} (RNC {RncEmisor}, Ambiente {Ambiente}) no se confirmó tras {Hours}h; marcado como Expired.",
                            doc.ENcf, doc.RncEmisor, doc.Ambiente, age.TotalHours);
                    }
                    else
                    {
                        doc.State = "RequiresManualReview";
                        logger.LogCritical(
                            "e-CF {ENcf} (RNC {RncEmisor}) lleva {Hours}h sin confirmación definitiva de DGII " +
                            "(ventana de {MaxHours}h agotada); requiere revisión manual.",
                            doc.ENcf, doc.RncEmisor, age.TotalHours, options.MaxPollingWindow.TotalHours);
                    }
                    continue;
                }

                try
                {
                    var clientToUse = ecfClient;
                    if (signerResolver != null && !string.IsNullOrWhiteSpace(doc.RncEmisor))
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
                        }
                        catch (Exception resolveEx)
                        {
                            logger.LogDebug(resolveEx, "No se pudo resolver signer dinámico para {Rnc}; usando ecfClient default.", doc.RncEmisor);
                        }
                    }

                    string? estado = null;
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
                                    doc.DgiiResponseXml = $"[{resultado.Estado}] " + string.Join("; ", resultado.Mensajes.Select(m => $"[{m.Codigo}] {m.Valor}"));
                                }
                                else
                                {
                                    doc.DgiiResponseXml = $"Estado DGII: {estado}";
                                }
                            }
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
                            doc.DgiiResponseXml = $"Estado DGII: {estado}";
                        }
                    }

                    doc.LastStatusCheckAt = now;
                    doc.StatusCheckAttempts++;

                    if (estado != null && AcceptedEstados.Contains(estado))
                    {
                        doc.State = "AcceptedByDgii";
                        logger.LogInformation("e-CF {ENcf}: DGII confirmó '{Estado}'.", doc.ENcf, estado);
                    }
                    else if (estado != null && RejectedEstados.Contains(estado))
                    {
                        doc.State = "RejectedByDgii";
                        logger.LogCritical(
                            "ALERTA: e-CF {ENcf} (RNC {RncEmisor}) fue aceptado en recepción y luego " +
                            "RECHAZADO por DGII tras verificación posterior. Requiere atención — el " +
                            "comprobante no tiene validez fiscal.",
                            doc.ENcf, doc.RncEmisor);
                    }
                    // Else (No encontrado / En proceso / unrecognized): stays SentToDgii, retried next pass.
                }
                catch (Exception ex)
                {
                    // A transport failure is not a DGII verdict — advance the counters (so the
                    // MaxPollingWindow clock still runs and this doesn't retry every single pass
                    // forever) but leave State untouched.
                    doc.LastStatusCheckAt = now;
                    doc.StatusCheckAttempts++;
                    logger.LogWarning(ex, "Fallo consultando estado DGII para e-CF {ENcf}; se reintentará.", doc.ENcf);
                }

                // Checkpoint each document update so crashes or transient failures don't clobber batch progress (MED-018)
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception saveEx)
                {
                    logger.LogError(saveEx, "Error persistiendo reconciliación para comprobante {ENcf}", doc.ENcf);
                }
            }

            return processed;
        }
    }
}
