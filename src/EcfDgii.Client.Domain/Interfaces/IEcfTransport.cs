using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IEcfTransport
    {
        Task<EcfRecepcionResponse> SendEcfAsync(
            string xmlContent, string fileName, CancellationToken ct = default);

        Task<RfceRecepcionResponse> SendRfceAsync(
            string xmlContent, string fileName, CancellationToken ct = default);

        Task<ConsultaResultadoResponse> ConsultarResultadoAsync(
            string trackId, CancellationToken ct = default);

        Task<ConsultaEstadoResponse> ConsultarEstadoAsync(
            ConsultaEstadoRequest request, CancellationToken ct = default);

        Task<List<TrackIdDetalle>> ConsultarTrackIdsAsync(
            string rncEmisor, string eNcf, CancellationToken ct = default);

        Task<RfceConsultaResponse> ConsultarRfceAsync(
            string rncEmisor, string eNcf, string codigoSeguridad,
            CancellationToken ct = default);

        Task<AprobacionComercialResponse> SendAprobacionComercialAsync(
            string xmlContent, string fileName, CancellationToken ct = default);

        Task<AnulacionResponse> AnularRangosAsync(
            string xmlContent, CancellationToken ct = default);

        Task<List<DirectorioContribuyente>> ConsultarDirectorioAsync(
            CancellationToken ct = default);

        Task<DirectorioContribuyente> ConsultarDirectorioPorRncAsync(
            string rnc, CancellationToken ct = default);

        Task<TimbreResponse> ConsultarTimbreAsync(
            TimbreEcfRequest request, CancellationToken ct = default);

        Task<TimbreFcResponse> ConsultarTimbreFcAsync(
            TimbreFcRequest request, CancellationToken ct = default);

        Task<List<EstatusServicio>> ConsultarEstatusServiciosAsync(
            CancellationToken ct = default);

        Task<List<VentanaMantenimiento>> ConsultarVentanasMantenimientoAsync(
            CancellationToken ct = default);

        Task<string> VerificarEstadoAmbienteAsync(
            AmbienteEnum ambiente, CancellationToken ct = default);
    }

    public enum AmbienteEnum
    {
        PreCertificacion = 1,
        Certificacion = 2,
        Produccion = 3
    }
}
