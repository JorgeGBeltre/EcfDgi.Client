using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Ecf.Commands.SendEcf
{
    public class SendEcfCommandHandler : IRequestHandler<SendEcfCommand, Result<EcfRecepcionResponse>>
    {
        private readonly IEcfClient _ecfClient;
        private readonly IEcfDocumentRepository _documentRepository;
        private readonly IUnitOfWork _unitOfWork;

        public SendEcfCommandHandler(
            IEcfClient ecfClient,
            IEcfDocumentRepository documentRepository,
            IUnitOfWork unitOfWork)
        {
            _ecfClient = ecfClient;
            _documentRepository = documentRepository;
            _unitOfWork = unitOfWork;
        }

        public async Task<Result<EcfRecepcionResponse>> Handle(SendEcfCommand request, CancellationToken cancellationToken)
        {
            try
            {
                // Send via SDK
                var response = await _ecfClient.SendEcfAsync(request.XmlContent, request.FileName, cancellationToken);

                // Check error returned in response payload (if any)
                var hasError = !string.IsNullOrEmpty(response.Error);

                // Save submission in database
                var doc = new EcfDocument
                {
                    ENcf = request.ENcf,
                    RncEmisor = request.RncEmisor,
                    RncComprador = request.RncComprador,
                    TrackId = response.TrackId,
                    State = hasError ? "Rechazado" : "Recibido",
                    TotalAmount = request.TotalAmount,
                    ItbisAmount = request.ItbisAmount,
                    XmlContent = request.XmlContent,
                    ReceiptDate = DateTime.UtcNow
                };

                await _documentRepository.AddAsync(doc, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);

                if (hasError)
                {
                    return Result<EcfRecepcionResponse>.Failure(response.Mensaje ?? response.Error);
                }

                return Result<EcfRecepcionResponse>.Success(response);
            }
            catch (Exception ex)
            {
                return Result<EcfRecepcionResponse>.Failure($"Failed to send e-CF: {ex.Message}");
            }
        }
    }
}
