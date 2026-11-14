using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using EcfDgii.Client.Application.Common.Interfaces;
using EcfDgii.Client.Application.Ecf.Commands.SendEcf;
using EcfDgii.Client.Application.Ecf.Commands.SendRfce;
using EcfDgii.Client.Application.Ecf.Queries.GetEcfStatus;
using EcfDgii.Client.Domain.Common;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Dgii;

namespace EcfDgii.Client.Api.Controllers
{
    [Authorize(Policy = "UserOrWorker")]
    public class EcfController : ApiControllerBase
    {
        [HttpPost("send")]
        public async Task<ActionResult<EcfRecepcionResponse>> SendEcf([FromBody] SendEcfCommand command)
        {
            if (command == null)
            {
                return BadRequest(new { error = "El cuerpo de la solicitud no puede estar vacío o malformado." });
            }

            if (!Request.Headers.ContainsKey("Idempotency-Key") && !Request.Headers.ContainsKey("X-Idempotency-Key"))
            {
                return BadRequest(new { error = "Header 'Idempotency-Key' is required for POST /api/ecf/send." });
            }

            var tenantId = !string.IsNullOrWhiteSpace(command.TenantId)
                ? command.TenantId
                : (Request.Headers.TryGetValue("X-Tenant-Id", out var hTenant) ? hTenant.ToString() : null);
            var env = !string.IsNullOrWhiteSpace(command.Ambiente)
                ? command.Ambiente
                : (Request.Headers.TryGetValue("X-Environment", out var hEnv) ? hEnv.ToString() : null);

            var updatedCommand = command with { TenantId = tenantId, Ambiente = env };
            var result = await Mediator.Send(updatedCommand);

            if (result.IsFailure)
            {
                return BadRequest(new { error = result.Error });
            }

            return Ok(result.Value);
        }

        [HttpPost("send-rfce")]
        public async Task<ActionResult<RfceRecepcionResponse>> SendRfce([FromBody] SendRfceCommand command)
        {
            if (command == null)
            {
                return BadRequest(new { error = "El cuerpo de la solicitud no puede estar vacío o malformado." });
            }

            if (string.IsNullOrWhiteSpace(command.TenantId) && Request.Headers.TryGetValue("X-Tenant-Id", out var hTenant))
            {
                command.TenantId = hTenant.ToString();
            }
            if (string.IsNullOrWhiteSpace(command.Ambiente) && Request.Headers.TryGetValue("X-Environment", out var hEnv))
            {
                command.Ambiente = hEnv.ToString();
            }

            var result = await Mediator.Send(command);

            if (result.IsFailure)
            {
                return BadRequest(new { error = result.Error });
            }

            return Ok(result.Value);
        }

        [HttpGet("status")]
        public async Task<ActionResult<ConsultaEstadoResponse>> GetStatus(
            [FromQuery] string rncEmisor,
            [FromQuery] string eNcf)
        {
            var result = await Mediator.Send(new GetEcfStatusQuery(rncEmisor, eNcf));

            if (result.IsFailure)
            {
                return BadRequest(new { error = result.Error });
            }

            return Ok(result.Value);
        }

        [HttpGet("services-status")]
        public async Task<ActionResult<List<DgiiServiceHealthDto>>> GetServicesStatus(
            [FromQuery] string? environment = null,
            [FromServices] IEcfClient? ecfClient = null,
            [FromServices] ICacheService? cacheService = null,
            [FromServices] IOptions<EcfClientOptions>? options = null,
            CancellationToken ct = default)
        {
            var rawEnv = !string.IsNullOrWhiteSpace(environment)
                ? environment
                : (Request?.Headers != null && Request.Headers.TryGetValue("X-Environment", out var hEnv) ? hEnv.ToString() : null);

            var defaultEnv = options?.Value.Environment ?? EcfEnvironment.Test;
            var resolvedEnv = !string.IsNullOrWhiteSpace(rawEnv)
                ? EcfEnvironmentHelper.ResolveEcfEnvironment(rawEnv, defaultEnv)
                : defaultEnv;

            var ambiente = resolvedEnv switch
            {
                EcfEnvironment.Test => AmbienteEnum.PreCertificacion,
                EcfEnvironment.Cert => AmbienteEnum.Certificacion,
                EcfEnvironment.Prod => AmbienteEnum.Produccion,
                _ => AmbienteEnum.PreCertificacion
            };

            var cacheKey = $"ecf:services:probe:{(int)ambiente}";
            if (cacheService != null)
            {
                var cached = await cacheService.GetAsync<List<DgiiServiceHealthDto>>(cacheKey, ct);
                if (cached != null && cached.Count > 0)
                {
                    return Ok(cached);
                }
            }

            var envConfig = EcfEnvironmentConfig.GetConfig(ambiente);
            var statusUrl = $"{envConfig.EstatusServiciosUrl}/api/estatusservicios/obtenerestatus";
            var nowIso = DateTime.UtcNow.ToString("o");
            bool gatewayOnline = false;
            int latencyMs = 0;

            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(3));

                var sw = Stopwatch.StartNew();
                using (var req1 = new HttpRequestMessage(HttpMethod.Get, statusUrl))
                using (var res1 = await EcfClient.SharedHttpClient.SendAsync(req1, HttpCompletionOption.ResponseHeadersRead, probeCts.Token))
                {
                    sw.Stop();
                    gatewayOnline = (int)res1.StatusCode < 500;
                    latencyMs = Math.Max(1, (int)sw.ElapsedMilliseconds);
                }

                if (gatewayOnline && latencyMs > 100 && !probeCts.IsCancellationRequested)
                {
                    var warmSw = Stopwatch.StartNew();
                    using var req2 = new HttpRequestMessage(HttpMethod.Get, statusUrl);
                    using var res2 = await EcfClient.SharedHttpClient.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, probeCts.Token);
                    warmSw.Stop();
                    if ((int)res2.StatusCode < 500)
                    {
                        latencyMs = Math.Max(1, (int)warmSw.ElapsedMilliseconds);
                    }
                }
            }
            catch
            {
                gatewayOnline = false;
                latencyMs = 0;
            }

            List<EstatusServicio>? officialStatuses = null;
            if (gatewayOnline && ecfClient != null)
            {
                try
                {
                    using var officialCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    officialCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                    officialStatuses = await ecfClient.ConsultarEstatusServiciosAsync(officialCts.Token);
                }
                catch
                {
                }
            }

            bool IsOfficialAvailable(string keyword)
            {
                if (!gatewayOnline)
                {
                    return false;
                }
                if (officialStatuses == null || officialStatuses.Count == 0)
                {
                    return true;
                }
                var envMatches = officialStatuses
                    .Where(s => string.IsNullOrWhiteSpace(s.Ambiente) ||
                                s.Ambiente.Contains(ambiente.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                (ambiente == AmbienteEnum.PreCertificacion && s.Ambiente.Contains("PreCert", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                var pool = envMatches.Count > 0 ? envMatches : officialStatuses;
                var match = pool.FirstOrDefault(s => s.Servicio != null && s.Servicio.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (match == null || string.IsNullOrWhiteSpace(match.Estatus))
                {
                    return true;
                }
                return !match.Estatus.Contains("No Disponible", StringComparison.OrdinalIgnoreCase) &&
                       !match.Estatus.Contains("Mantenimiento", StringComparison.OrdinalIgnoreCase);
            }

            var items = new List<DgiiServiceHealthDto>
            {
                new("Recepción e-CF", IsOfficialAvailable("Recepci"), gatewayOnline ? latencyMs : 0, nowIso),
                new("Consulta Estado", IsOfficialAvailable("Consulta Estado"), gatewayOnline ? latencyMs : 0, nowIso),
                new("Timbre / QR", IsOfficialAvailable("Timbre"), gatewayOnline ? latencyMs : 0, nowIso),
                new("Directorio Contribuyentes", IsOfficialAvailable("Directorio"), gatewayOnline ? latencyMs : 0, nowIso),
                new("Aprobación Comercial", IsOfficialAvailable("Aprobaci"), gatewayOnline ? latencyMs : 0, nowIso)
            };

            if (gatewayOnline && cacheService != null)
            {
                await cacheService.SetAsync(cacheKey, items, TimeSpan.FromSeconds(45), ct);
            }

            return Ok(items);
        }
    }

    public sealed record DgiiServiceHealthDto(
        string Servicio,
        bool Disponible,
        int Latencia,
        string UltimaVerificacion);
}
