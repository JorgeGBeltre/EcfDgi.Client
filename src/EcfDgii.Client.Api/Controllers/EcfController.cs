using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EcfDgii.Client.Application.Ecf.Commands.SendEcf;
using EcfDgii.Client.Application.Ecf.Commands.SendRfce;
using EcfDgii.Client.Application.Ecf.Queries.GetEcfStatus;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Api.Controllers
{
    [Authorize(Policy = "UserOrWorker")]
    public class EcfController : ApiControllerBase
    {
        [HttpPost("send")]
        public async Task<ActionResult<EcfRecepcionResponse>> SendEcf([FromBody] SendEcfCommand command)
        {
            if (!Request.Headers.ContainsKey("Idempotency-Key") && !Request.Headers.ContainsKey("X-Idempotency-Key"))
            {
                return BadRequest(new { error = "Header 'Idempotency-Key' is required for POST /api/ecf/send." });
            }

            ModelState.Clear();
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
            ModelState.Clear();
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
    }
}
