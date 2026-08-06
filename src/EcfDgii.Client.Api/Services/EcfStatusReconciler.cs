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

namespace EcfDgii.Client.Api.Services
{
    public sealed record EcfStatusPollingOptions(
        TimeSpan PollingInterval,
        TimeSpan MinDocumentAge,
        TimeSpan MaxPollingWindow)
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
            MaxPollingWindow: TimeSpan.FromHours(72));
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
        ILogger<EcfStatusReconciler> logger)
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

            var due = await db.EcfDocuments
                .Where(d => d.State == "SentToDgii"
                         && d.SentToDgiiAt != null
                         && d.SentToDgiiAt <= minAgeCutoff
                         && (d.LastStatusCheckAt == null || d.LastStatusCheckAt <= pollDueCutoff))
                .ToListAsync(ct);

            var processed = 0;

            foreach (var doc in due)
            {
                processed++;
                var age = now - doc.SentToDgiiAt!.Value;

                if (age >= options.MaxPollingWindow)
                {
                    doc.State = "RequiresManualReview";
                    doc.LastStatusCheckAt = now;
                    logger.LogCritical(
                        "e-CF {ENcf} (RNC {RncEmisor}) lleva {Hours}h sin confirmación definitiva de DGII " +
                        "(ventana de {MaxHours}h agotada); requiere revisión manual.",
                        doc.ENcf, doc.RncEmisor, age.TotalHours, options.MaxPollingWindow.TotalHours);
                    continue;
                }

                try
                {
                    var response = await ecfClient.ConsultarEstadoAsync(
                        doc.RncEmisor, doc.ENcf, doc.RncComprador, doc.SecurityCode, ct);

                    doc.LastStatusCheckAt = now;
                    doc.StatusCheckAttempts++;

                    var estado = response?.Estado?.Trim();
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
            }

            if (processed > 0)
            {
                await db.SaveChangesAsync(ct);
            }

            return processed;
        }
    }
}
