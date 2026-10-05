using System;
using System.Linq;
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
            var tenantId = !string.IsNullOrWhiteSpace(request.TenantId) ? request.TenantId : request.RncEmisor;
            var sourceTxnId = !string.IsNullOrWhiteSpace(request.SourceTxnId)
                ? request.SourceTxnId
                : (!string.IsNullOrWhiteSpace(request.ENcf) ? request.ENcf : Guid.NewGuid().ToString("N"));
            var editSequence = !string.IsNullOrWhiteSpace(request.EditSequence) ? request.EditSequence : "1";
            var ambiente = !string.IsNullOrWhiteSpace(request.Ambiente) ? request.Ambiente : "Certificacion";

            // 1. Pre-commit document before transmitting to DGII to ensure idempotency and unique index protection
            var doc = new EcfDocument
            {
                TenantId = tenantId,
                SourceTxnId = sourceTxnId,
                EditSequence = editSequence,
                Ambiente = ambiente,
                ENcf = request.ENcf,
                RncEmisor = request.RncEmisor,
                RncComprador = request.RncComprador,
                State = "PreSend",
                TotalAmount = request.TotalAmount,
                ItbisAmount = request.ItbisAmount,
                XmlContent = request.XmlContent,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                await _documentRepository.AddAsync(doc, cancellationToken);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception dbEx)
            {
                return Result<EcfRecepcionResponse>.Failure($"Failed to persist pre-send document record: {dbEx.Message}");
            }

            // 2. Transmit to DGII
            EcfRecepcionResponse response;
            try
            {
                doc.SentToDgiiAt = DateTime.UtcNow;
                response = await _ecfClient.SendEcfAsync(request.XmlContent, request.FileName, cancellationToken);
            }
            catch (Exception ex)
            {
                // Network or transport fault: Mark state Uncertain so reconciler can poll and clarify
                doc.State = "Uncertain";
                doc.DgiiResponseXml = $"Error de transmisión: {ex.Message}";
                try
                {
                    _documentRepository.Update(doc);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                }
                catch { /* Ignore fallback update error */ }

                return Result<EcfRecepcionResponse>.Failure($"Transmission fault while sending e-CF: {ex.Message}");
            }

            // 3. Process DGII response
            var hasError = !string.IsNullOrEmpty(response.Error);
            doc.TrackId = response.TrackId;
            doc.State = hasError ? "Rechazado" : "Recibido";
            doc.ReceiptDate = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(response.Mensaje))
            {
                doc.DgiiResponseXml = response.Mensaje;
            }

            // 4. Update local document; if post-transmission persistence fails, do NOT fail the send result
            // because DGII has already processed and issued TrackId!
            try
            {
                _documentRepository.Update(doc);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (Exception)
            {
                // Swallow post-transmission DB persistence exception; reconciler will sync state via TrackId/eNCF
            }

            if (hasError)
            {
                return Result<EcfRecepcionResponse>.Failure(response.Mensaje ?? response.Error);
            }

            return Result<EcfRecepcionResponse>.Success(response);
        }
    }
}
