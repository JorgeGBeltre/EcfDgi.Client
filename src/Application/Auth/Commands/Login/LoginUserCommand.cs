using MediatR;
using EcfDgii.Client.Application.Auth.Common;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Auth.Commands.Login
{
    public record LoginUserCommand(string Username, string Password) : IRequest<Result<AuthResponseDto>>;
}
