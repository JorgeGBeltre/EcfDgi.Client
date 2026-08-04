using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Ecf.Queries.GetEcfStatus
{
    public class GetEcfStatusQueryHandler : IRequestHandler<GetEcfStatusQuery, Result<ConsultaEstadoResponse>>
    {
        private readonly IEcfClient _ecfClient;
        private readonly IEcfDocumentRepository _documentRepository;
        private readonly IUnitOfWork _unitOfWork;

        public GetEcfStatusQueryHandler(
            IEcfClient ecfClient,
            IEcfDocumentRepository documentRepository,
            IUnitOfWork unitOfWork)
        {
            _ecfClient = ecfClient;
            _documentRepository = documentRepository;
            _unitOfWork = unitOfWork;
        }

        public async Task<Result<ConsultaEstadoResponse>> Handle(GetEcfStatusQuery request, CancellationToken cancellationToken)
        {
            try
            {
                // 1. Try local DB search
                var localDoc = await _documentRepository.GetByENcfAsync(request.ENcf, cancellationToken);
                if (localDoc != null && localDoc.State == "Aceptado")
                {
                    // Map to response model
                    var localResponse = new ConsultaEstadoResponse
                    {
                        Codigo = 0,
                        Estado = localDoc.State,
                        RncEmisor = localDoc.RncEmisor,
                        NcfElectronico = localDoc.ENcf,
                        MontoTotal = localDoc.TotalAmount,
                        TotalITBIS = localDoc.ItbisAmount,
                        RncComprador = localDoc.RncComprador ?? string.Empty,
                        CodigoSeguridad = localDoc.SecurityCode ?? string.Empty,
                        FechaEmision = localDoc.CreatedAt.ToString("o")
                    };

                    return Result<ConsultaEstadoResponse>.Success(localResponse);
                }

                // 2. Fetch from DGII
                // For a live query, we need to supply basic fields. If we don't have RncComprador/CodigoSeguridad locally, they default to null
                var rncComprador = localDoc?.RncComprador;
                var secCode = localDoc?.SecurityCode;

                var liveResponse = await _ecfClient.ConsultarEstadoAsync(
                    request.RncEmisor,
                    request.ENcf,
                    rncComprador,
                    secCode,
                    cancellationToken);

                // 3. Update local DB if status changed
                if (localDoc != null && localDoc.State != liveResponse.Estado)
                {
                    localDoc.State = liveResponse.Estado;
                    _documentRepository.Update(localDoc);
                    await _unitOfWork.SaveChangesAsync(cancellationToken);
                }

                return Result<ConsultaEstadoResponse>.Success(liveResponse);
            }
            catch (Exception ex)
            {
                return Result<ConsultaEstadoResponse>.Failure($"Failed to retrieve status: {ex.Message}");
            }
        }
    }
}
