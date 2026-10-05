using System.Text.Json.Serialization;
using MediatR;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Ecf.Commands.SendRfce
{
    public record SendRfceCommand : IRequest<Result<RfceRecepcionResponse>>
    {
        public Rfce RfceModel { get; set; } = new Rfce();
        public string? TenantId { get; set; }
        public string? SourceTxnId { get; set; }
        public string? Ambiente { get; set; }
        public string? EditSequence { get; set; }

        public SendRfceCommand() { }

        [JsonConstructor]
        public SendRfceCommand(Rfce rfceModel, string? tenantId = null, string? sourceTxnId = null, string? ambiente = null, string? editSequence = null)
        {
            RfceModel = rfceModel;
            TenantId = tenantId;
            SourceTxnId = sourceTxnId;
            Ambiente = ambiente;
            EditSequence = editSequence;
        }
    }
}
