using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IEcfClient
    {
        Task<EcfRecepcionResponse> SendEcfAsync(string xmlContent, string fileName, CancellationToken ct = default);
        Task<RfceRecepcionResponse> SendRfceAsync(Rfce rfce, CancellationToken ct = default);
        Task<ConsultaResultadoResponse> ConsultarResultadoAsync(string trackId, CancellationToken ct = default);
        Task<ConsultaEstadoResponse> ConsultarEstadoAsync(string rncEmisor, string eNcf, string? rncComprador = null, string? codigoSeguridad = null, CancellationToken ct = default);
        Task<List<TrackIdDetalle>> ConsultarTrackIdsAsync(string rncEmisor, string eNcf, CancellationToken ct = default);
        Task<RfceConsultaResponse> ConsultarRfceAsync(string rncEmisor, string eNcf, string codigoSeguridad, CancellationToken ct = default);
        Task<TimbreResponse> ValidarTimbreEcfAsync(TimbreEcfRequest request, CancellationToken ct = default);
        Task<TimbreFcResponse> ValidarTimbreFcAsync(TimbreFcRequest request, CancellationToken ct = default);
        Task<List<DirectorioContribuyente>> ConsultarDirectorioAsync(CancellationToken ct = default);
        Task<List<EstatusServicio>> ConsultarEstatusServiciosAsync(CancellationToken ct = default);
        Task<List<VentanaMantenimiento>> ConsultarVentanasMantenimientoAsync(CancellationToken ct = default);
        Task<string> VerificarEstadoAmbienteAsync(AmbienteEnum ambiente, CancellationToken ct = default);
        Task<AnulacionResponse> AnularRangosAsync(string xmlContent, CancellationToken ct = default);
    }
}
