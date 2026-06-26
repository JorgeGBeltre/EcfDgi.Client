using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Ecf.Commands.SendRfce
{
    public class SendRfceCommandHandler : IRequestHandler<SendRfceCommand, Result<RfceRecepcionResponse>>
    {
        private readonly IEcfClient _ecfClient;
        private readonly IEcfXmlSerializer _serializer;
        private readonly IEcfDocumentRepository _documentRepository;
        private readonly IUnitOfWork _unitOfWork;

        public SendRfceCommandHandler(
            IEcfClient ecfClient,
            IEcfXmlSerializer serializer,
            IEcfDocumentRepository documentRepository,
            IUnitOfWork unitOfWork)
        {
            _ecfClient = ecfClient;
            _serializer = serializer;
            _documentRepository = documentRepository;
            _unitOfWork = unitOfWork;
        }

        public async Task<Result<RfceRecepcionResponse>> Handle(SendRfceCommand request, CancellationToken cancellationToken)
        {
            try
            {
                var rfce = request.RfceModel;

                // Call SDK to validate and transmit
                var response = await _ecfClient.SendRfceAsync(rfce, cancellationToken);

                // Serialize locally to save the original XML generated
                var xmlContent = _serializer.Serialize(rfce);

                // Save submission status in database
                var doc = new EcfDocument
                {
                    ENcf = rfce.Encabezado.IdDoc.ENcf,
                    RncEmisor = rfce.Encabezado.Emisor.RncEmisor,
                    RncComprador = rfce.Encabezado.Comprador?.RncComprador,
                    TrackId = null, // RFCE responses don't return TrackId immediately, they return code/estado
                    State = response.Estado,
                    TotalAmount = rfce.Encabezado.Totales.MontoTotal,
                    ItbisAmount = rfce.Encabezado.Totales.TotalITBIS ?? 0,
                    SecurityCode = rfce.Encabezado.Totales.CodigoSeguridadeCF,
                    XmlContent = xmlContent,
                    ReceiptDate = DateTime.UtcNow
                };

                await _documentRepository.AddAsync(doc, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                if (response.Estado == "Rechazado")
                {
                    var msg = response.Mensajes != null && response.Mensajes.Count > 0
                        ? response.Mensajes[0].Valor
                        : "Rechazado por DGII";
                    return Result<RfceRecepcionResponse>.Failure(msg);
                }

                return Result<RfceRecepcionResponse>.Success(response);
            }
            catch (Exception ex)
            {
                return Result<RfceRecepcionResponse>.Failure($"Failed to send RFCE: {ex.Message}");
            }
        }
    }
}
