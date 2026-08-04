using System.Text.Json.Serialization;
using MediatR;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Ecf.Commands.SendRfce
{
    public record SendRfceCommand : IRequest<Result<RfceRecepcionResponse>>
    {
        public Rfce RfceModel { get; set; } = new Rfce();

        public SendRfceCommand() { }

        [JsonConstructor]
        public SendRfceCommand(Rfce rfceModel)
        {
            RfceModel = rfceModel;
        }
    }
}
