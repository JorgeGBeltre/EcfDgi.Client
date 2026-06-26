using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EcfDgii.Client.Application.Ecf.Commands.SendEcf;
using EcfDgii.Client.Application.Ecf.Commands.SendRfce;
using EcfDgii.Client.Application.Ecf.Queries.GetEcfStatus;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Api.Controllers
{
    [Authorize]
    public class EcfController : ApiControllerBase
    {
        [HttpPost("send")]
        public async Task<ActionResult<EcfRecepcionResponse>> SendEcf(SendEcfCommand command)
        {
            var result = await Mediator.Send(command);

            if (result.IsFailure)
            {
                return BadRequest(new { error = result.Error });
            }

            return Ok(result.Value);
        }

        [HttpPost("send-rfce")]
        public async Task<ActionResult<RfceRecepcionResponse>> SendRfce(SendRfceCommand command)
        {
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
