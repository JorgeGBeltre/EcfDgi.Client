using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Application.Common.Interfaces;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Infrastructure.Dgii
{
    public class CachedEcfClient : IEcfClient
    {
        private readonly IEcfClient _innerClient;
        private readonly ICacheService _cacheService;

        public CachedEcfClient(IEcfClient innerClient, ICacheService cacheService)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        }

        public Task<EcfRecepcionResponse> SendEcfAsync(string xmlContent, string fileName, CancellationToken ct = default)
        {
            return _innerClient.SendEcfAsync(xmlContent, fileName, ct);
        }

        public Task<RfceRecepcionResponse> SendRfceAsync(Rfce rfce, CancellationToken ct = default)
        {
            return _innerClient.SendRfceAsync(rfce, ct);
        }

        public Task<AprobacionComercialResponse> SendAprobacionComercialAsync(string xmlContent, string fileName, CancellationToken ct = default)
        {
            return _innerClient.SendAprobacionComercialAsync(xmlContent, fileName, ct);
        }

        public Task<ConsultaResultadoResponse> ConsultarResultadoAsync(string trackId, CancellationToken ct = default)
        {
            return _innerClient.ConsultarResultadoAsync(trackId, ct);
        }

        public Task<ConsultaEstadoResponse> ConsultarEstadoAsync(string rncEmisor, string eNcf, string? rncComprador = null, string? codigoSeguridad = null, CancellationToken ct = default)
        {
            return _innerClient.ConsultarEstadoAsync(rncEmisor, eNcf, rncComprador, codigoSeguridad, ct);
        }

        public Task<List<TrackIdDetalle>> ConsultarTrackIdsAsync(string rncEmisor, string eNcf, CancellationToken ct = default)
        {
            return _innerClient.ConsultarTrackIdsAsync(rncEmisor, eNcf, ct);
        }

        public Task<RfceConsultaResponse> ConsultarRfceAsync(string rncEmisor, string eNcf, string codigoSeguridad, CancellationToken ct = default)
        {
            return _innerClient.ConsultarRfceAsync(rncEmisor, eNcf, codigoSeguridad, ct);
        }

        public Task<TimbreResponse> ValidarTimbreEcfAsync(TimbreEcfRequest request, CancellationToken ct = default)
        {
            return _innerClient.ValidarTimbreEcfAsync(request, ct);
        }

        public Task<TimbreFcResponse> ValidarTimbreFcAsync(TimbreFcRequest request, CancellationToken ct = default)
        {
            return _innerClient.ValidarTimbreFcAsync(request, ct);
        }

        public async Task<List<DirectorioContribuyente>> ConsultarDirectorioAsync(CancellationToken ct = default)
        {
            const string cacheKey = "ecf:directory:all";
            var cachedDirectory = await _cacheService.GetAsync<List<DirectorioContribuyente>>(cacheKey, ct);
            if (cachedDirectory != null && cachedDirectory.Count > 0)
            {
                return cachedDirectory;
            }

            var result = await _innerClient.ConsultarDirectorioAsync(ct);
            if (result != null && result.Count > 0)
            {
                await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromHours(24), ct);
            }

            return result ?? new List<DirectorioContribuyente>();
        }

        public async Task<List<EstatusServicio>> ConsultarEstatusServiciosAsync(CancellationToken ct = default)
        {
            const string cacheKey = "ecf:services:status";
            var cachedStatus = await _cacheService.GetAsync<List<EstatusServicio>>(cacheKey, ct);
            if (cachedStatus != null && cachedStatus.Count > 0)
            {
                return cachedStatus;
            }

            var result = await _innerClient.ConsultarEstatusServiciosAsync(ct);
            if (result != null && result.Count > 0)
            {
                await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromMinutes(5), ct);
            }

            return result ?? new List<EstatusServicio>();
        }

        public async Task<List<VentanaMantenimiento>> ConsultarVentanasMantenimientoAsync(CancellationToken ct = default)
        {
            const string cacheKey = "ecf:maintenance:windows";
            var cachedWindows = await _cacheService.GetAsync<List<VentanaMantenimiento>>(cacheKey, ct);
            if (cachedWindows != null && cachedWindows.Count > 0)
            {
                return cachedWindows;
            }

            var result = await _innerClient.ConsultarVentanasMantenimientoAsync(ct);
            if (result != null && result.Count > 0)
            {
                await _cacheService.SetAsync(cacheKey, result, TimeSpan.FromHours(1), ct);
            }

            return result ?? new List<VentanaMantenimiento>();
        }

        public Task<string> VerificarEstadoAmbienteAsync(AmbienteEnum ambiente, CancellationToken ct = default)
        {
            return _innerClient.VerificarEstadoAmbienteAsync(ambiente, ct);
        }

        public Task<AnulacionResponse> AnularRangosAsync(string xmlContent, CancellationToken ct = default)
        {
            return _innerClient.AnularRangosAsync(xmlContent, ct);
        }
    }
}
