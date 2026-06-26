using MediatR;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Ecf.Queries.GetEcfStatus
{
    public record GetEcfStatusQuery(string RncEmisor, string ENcf) : IRequest<Result<ConsultaEstadoResponse>>;
}
