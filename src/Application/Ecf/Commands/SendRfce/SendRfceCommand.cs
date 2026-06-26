using MediatR;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Ecf.Commands.SendRfce
{
    public record SendRfceCommand(Rfce RfceModel) : IRequest<Result<RfceRecepcionResponse>>;
}
