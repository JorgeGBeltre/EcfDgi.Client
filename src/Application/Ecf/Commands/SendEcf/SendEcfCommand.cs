using MediatR;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Ecf.Commands.SendEcf
{
    public record SendEcfCommand(
        string XmlContent,
        string FileName,
        string RncEmisor,
        string ENcf,
        string? RncComprador,
        decimal TotalAmount,
        decimal ItbisAmount
    ) : IRequest<Result<EcfRecepcionResponse>>;
}
