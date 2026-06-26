using MediatR;
using EcfDgii.Client.Application.Auth.Common;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Auth.Commands.Register
{
    public record RegisterUserCommand(string Username, string Email, string Password, string Role) : IRequest<Result<AuthResponseDto>>;
}
