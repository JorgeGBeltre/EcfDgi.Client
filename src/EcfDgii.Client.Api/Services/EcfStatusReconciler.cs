using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Application.Common.Interfaces;
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
        int BatchSize = 50,
        TimeSpan? MinUncertainAge = null)
    {
        public static EcfStatusPollingOptions Default => new(
            PollingInterval: TimeSpan.FromSeconds(15),
            MinDocumentAge: TimeSpan.FromSeconds(10),
            MaxPollingWindow: TimeSpan.FromHours(72),
            BatchSize: 50,
            MinUncertainAge: TimeSpan.FromMinutes(2));
    }

    public sealed class EcfStatusReconciler(
        ApplicationDbContext db,
        IEcfClient ecfClient,
        IClock clock,
        EcfStatusPollingOptions options,
        ILogger<EcfStatusReconciler> logger,
        ITenantSignerResolver? signerResolver = null,
        ICacheService? cacheService = null)
    {
        private static readonly HashSet<string> AcceptedEstados = new(StringComparer.OrdinalIgnoreCase)
        {
            "Aceptado", "Aceptado condicional",
        };

        private static readonly HashSet<string> RejectedEstados = new(StringComparer.OrdinalIgnoreCase)
        {
            "Rechazado",
        };

        public async Task<int> ReconcileAsync(CancellationToken ct)
        {
            var now = clock.UtcNow.UtcDateTime;
            var minSignedCutoff = now - options.MinDocumentAge;
            var minUncertainCutoff = now - (options.MinUncertainAge ?? options.MinDocumentAge);
            var pollDueCutoff = now - options.PollingInterval;
            var batchSize = options.BatchSize > 0 ? options.BatchSize : 50;

            var due = await db.EcfDocuments
                .AsNoTracking()
                .Where(d =>
                    (((d.State == "Signed" || d.State == "SentToDgii")
                        && ((d.SentToDgiiAt != null && d.SentToDgiiAt <= minSignedCutoff) || (d.SentToDgiiAt == null && d.CreatedAt <= minSignedCutoff)))
                     || (d.State == "Uncertain"
                        && ((d.SentToDgiiAt != null && d.SentToDgiiAt <= minUncertainCutoff) || (d.SentToDgiiAt == null && d.CreatedAt <= minUncertainCutoff))))
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
                    d.TotalAmount,
                    d.SentToDgiiAt,
                    d.CreatedAt,
                    d.LastStatusCheckAt,
                    d.StatusCheckAttempts))
                .ToListAsync(ct);

            var processed = 0;
            var dynamicClients = new Dictionary<string, IEcfClient>(StringComparer.OrdinalIgnoreCase);

            try
            {
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

                        await UpdateDocStatusAsync(doc.Id, finalState, now, doc.StatusCheckAttempts, sentTime, null, null, ct);
                        continue;
                    }

                    string? dgiiResponseXml = null;
                    string? estado = null;
                    string? newTrackId = null;
                    var attempts = doc.StatusCheckAttempts + 1;
                    var newState = doc.State;

                    try
                    {
                        var clientToUse = ecfClient;
                        if (signerResolver != null && !string.IsNullOrWhiteSpace(doc.RncEmisor))
                        {
                            var env = EcfEnvironmentHelper.ResolveEcfEnvironment(doc.Ambiente);
                            var clientKey = $"{doc.RncEmisor}:{env}";
                            if (!dynamicClients.TryGetValue(clientKey, out clientToUse!))
                            {
                                try
                                {
                                    var dynamicSigner = await signerResolver.ResolveSignerAsync(doc.RncEmisor, ct);

                                    var clientOpts = new EcfClientOptions
                                    {
                                        RncEmisor = doc.RncEmisor,
                                        Environment = env,
                                        Mode = IntegrationMode.DgiiDirect,
                                        ValidateSchemasLocal = false
                                    };
                                    clientToUse = new EcfClient(clientOpts, signer: dynamicSigner, cacheService: cacheService);
                                    dynamicClients[clientKey] = clientToUse;
                                }
                                catch (Exception resolveEx)
                                {
                                    logger.LogDebug(resolveEx, "No se pudo resolver signer dinámico para {Rnc} ({Env}); usando ecfClient default.", doc.RncEmisor, env);
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
                        else if (string.Equals(doc.State, "Uncertain", StringComparison.OrdinalIgnoreCase)
                              && string.IsNullOrWhiteSpace(doc.TrackId)
                              && string.Equals(estado, "No encontrado", StringComparison.OrdinalIgnoreCase))
                        {
                            var signedPayloads = await db.EcfDocuments
                                .AsNoTracking()
                                .Where(d => d.Id == doc.Id)
                                .Select(d => new { d.SignedRfceContent, d.SignedXmlContent })
                                .FirstOrDefaultAsync(ct);

                            var isRfce = doc.ENcf.StartsWith("E32", StringComparison.OrdinalIgnoreCase) && doc.TotalAmount < 250000m;
                            var fileName = $"{doc.RncEmisor}{doc.ENcf}.xml";

                            if (isRfce && !string.IsNullOrWhiteSpace(signedPayloads?.SignedRfceContent))
                            {
                                var rfceResp = await clientToUse.SendRfceAsync(signedPayloads.SignedRfceContent, fileName, ct);
                                if (rfceResp != null && (rfceResp.Codigo == 1 || (rfceResp.Estado != null && AcceptedEstados.Contains(rfceResp.Estado.Trim()))))
                                {
                                    newState = "AcceptedByDgii";
                                    sentTime = now;
                                    dgiiResponseXml = $"Aceptado por DGII (RecepcionFC): {rfceResp.Estado?.Trim() ?? "Aceptado"}";
                                    logger.LogInformation(
                                        "e-CF de Consumo Uncertain {ENcf} (Ambiente {Ambiente}) retransmitido automáticamente a RecepcionFC y aceptado.",
                                        doc.ENcf, doc.Ambiente);
                                }
                                else if (rfceResp != null && rfceResp.Estado != null && RejectedEstados.Contains(rfceResp.Estado.Trim()))
                                {
                                    newState = "RejectedByDgii";
                                    dgiiResponseXml = rfceResp.Mensajes != null && rfceResp.Mensajes.Count > 0
                                        ? string.Join("; ", rfceResp.Mensajes.Select(m => $"[{m.Codigo}] {m.Valor}"))
                                        : rfceResp.Estado.Trim();
                                    logger.LogWarning(
                                        "e-CF de Consumo Uncertain {ENcf} (Ambiente {Ambiente}) retransmitido automáticamente a RecepcionFC y rechazado: {Errors}",
                                        doc.ENcf, doc.Ambiente, dgiiResponseXml);
                                }
                            }
                            else if (!isRfce && !string.IsNullOrWhiteSpace(signedPayloads?.SignedXmlContent))
                            {
                                var ecfResp = await clientToUse.SendEcfAsync(signedPayloads.SignedXmlContent, fileName, ct);
                                if (ecfResp != null && !string.IsNullOrWhiteSpace(ecfResp.TrackId))
                                {
                                    newState = "Signed";
                                    newTrackId = ecfResp.TrackId;
                                    sentTime = now;
                                    dgiiResponseXml = $"Recibido por DGII. TrackId: {ecfResp.TrackId}";
                                    logger.LogInformation(
                                        "e-CF estándar Uncertain {ENcf} (Ambiente {Ambiente}) retransmitido automáticamente a DGII. TrackId={TrackId}",
                                        doc.ENcf, doc.Ambiente, ecfResp.TrackId);
                                }
                                else if (ecfResp != null && (!string.IsNullOrWhiteSpace(ecfResp.Error) || (ecfResp.Mensaje != null && ecfResp.Mensaje.Contains("rechazado", StringComparison.OrdinalIgnoreCase))))
                                {
                                    newState = "RejectedByDgii";
                                    dgiiResponseXml = ecfResp.Mensaje ?? ecfResp.Error ?? "Rechazado por DGII";
                                    logger.LogWarning(
                                        "e-CF estándar Uncertain {ENcf} (Ambiente {Ambiente}) retransmitido automáticamente y rechazado por DGII: {Errors}",
                                        doc.ENcf, doc.Ambiente, dgiiResponseXml);
                                }
                            }
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

                    await UpdateDocStatusAsync(doc.Id, newState, now, attempts, sentTime, dgiiResponseXml, newTrackId, ct);
                }
            }
            finally
            {
                foreach (var disposable in dynamicClients.Values.OfType<IDisposable>())
                {
                    disposable.Dispose();
                }
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
            string? trackId,
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
                            .SetProperty(d => d.TrackId, d => trackId ?? d.TrackId)
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
                        if (trackId != null)
                        {
                            attached.TrackId = trackId;
                        }
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
        decimal TotalAmount,
        DateTime? SentToDgiiAt,
        DateTime CreatedAt,
        DateTime? LastStatusCheckAt,
        int StatusCheckAttempts);
}
