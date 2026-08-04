using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using EcfDgii.Client.Application.Auth.Commands.Login;
using EcfDgii.Client.Application.Auth.Commands.Register;
using EcfDgii.Client.Application.Auth.Common;

namespace EcfDgii.Client.Api.Controllers
{
    public class AuthController : ApiControllerBase
    {
        [HttpPost("register")]
        public async Task<ActionResult<AuthResponseDto>> Register(RegisterUserCommand command)
        {
            var result = await Mediator.Send(command);

            if (result.IsFailure)
            {
                return BadRequest(new { error = result.Error, errors = result.Errors });
            }

            return Ok(result.Value);
        }

        [HttpPost("login")]
        public async Task<ActionResult<AuthResponseDto>> Login(LoginUserCommand command)
        {
            var result = await Mediator.Send(command);

            if (result.IsFailure)
            {
                return Unauthorized(new { error = result.Error });
            }

            return Ok(result.Value);
        }
    }
}
