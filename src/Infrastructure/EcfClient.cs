using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Exceptions;
using EcfDgii.Client.Application.Services;
using EcfDgii.Client.Infrastructure.Dgii;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Infrastructure.Serialization;
using EcfDgii.Client.Infrastructure.Persistence;

namespace EcfDgii.Client
{
    public class EcfClient : IEcfClient
    {
        private readonly IEcfTransport _transport;
        private readonly EcfValidator _validator;
        private readonly EcfXmlSerializer _serializer;
        private readonly IEcfSequenceProvider _sequenceProvider;
        private readonly EcfClientOptions _options;

        private const decimal RfceThreshold = 250_000.00m;

        public EcfClient(EcfClientOptions? options = null, IEcfTransport? transport = null, IEcfSequenceProvider? sequenceProvider = null)
        {
            _options = options ?? new EcfClientOptions();
            _validator = new EcfValidator();
            _serializer = new EcfXmlSerializer();
            _sequenceProvider = sequenceProvider ?? new MemorySequenceProvider();

            if (transport != null)
            {
                _transport = transport;
                return;
            }

            var httpClient = new HttpClient();

            if (_options.Mode == IntegrationMode.DgiiDirect)
            {
                if (string.IsNullOrEmpty(_options.RncEmisor))
                    throw new InvalidOperationException("RncEmisor is required for Direct mode.");
                if (string.IsNullOrEmpty(_options.CertificatePath))
                    throw new InvalidOperationException("CertificatePath is required for Direct mode.");

                var signer = new EcfXmlSigner(_options.CertificatePath, _options.CertificatePassword ?? "");
                var envConfig = EcfEnvironmentConfig.GetConfig((AmbienteEnum)_options.Environment);
                var tokenManager = new EcfTokenManager(httpClient, signer, envConfig, _options.RncEmisor);

                _transport = new DgiiDirectTransport(httpClient, tokenManager, envConfig);
            }
            else
            {
                throw new NotSupportedException("Only DgiiDirect mode is supported.");
            }
        }

        public async Task<EcfRecepcionResponse> SendEcfAsync(string xmlContent, string fileName, CancellationToken ct = default)
        {
            return await _transport.SendEcfAsync(xmlContent, fileName, ct);
        }

        public async Task<RfceRecepcionResponse> SendRfceAsync(Rfce rfce, CancellationToken ct = default)
        {
            var validation = _validator.ValidateRfce(rfce);
            if (!validation.IsValid)
                throw new EcfValidationException(validation.Errors);

            var xml = _serializer.Serialize(rfce);
            var fileName = _serializer.GetFileName(rfce.Encabezado.Emisor.RncEmisor, rfce.Encabezado.IdDoc.ENcf);

            var response = await _transport.SendRfceAsync(xml, fileName, ct);

            if (_options.AutoRetryOnReuseableSequence && response.Estado == "Rechazado" && !response.SecuenciaUtilizada)
            {
                var newENcf = await _sequenceProvider.GetNextAsync(rfce.Encabezado.Emisor.RncEmisor, ct);
                rfce.Encabezado.IdDoc.ENcf = newENcf;
                return await SendRfceAsync(rfce, ct);
            }

            return response;
        }

        public Task<ConsultaResultadoResponse> ConsultarResultadoAsync(string trackId, CancellationToken ct = default) =>
            _transport.ConsultarResultadoAsync(trackId, ct);

        public Task<ConsultaEstadoResponse> ConsultarEstadoAsync(string rncEmisor, string eNcf, string? rncComprador = null, string? codigoSeguridad = null, CancellationToken ct = default) =>
            _transport.ConsultarEstadoAsync(new ConsultaEstadoRequest(rncEmisor, eNcf, rncComprador, codigoSeguridad), ct);

        public Task<List<TrackIdDetalle>> ConsultarTrackIdsAsync(string rncEmisor, string eNcf, CancellationToken ct = default) =>
            _transport.ConsultarTrackIdsAsync(rncEmisor, eNcf, ct);

        public Task<RfceConsultaResponse> ConsultarRfceAsync(string rncEmisor, string eNcf, string codigoSeguridad, CancellationToken ct = default) =>
            _transport.ConsultarRfceAsync(rncEmisor, eNcf, codigoSeguridad, ct);

        public Task<TimbreResponse> ValidarTimbreEcfAsync(TimbreEcfRequest request, CancellationToken ct = default) =>
            _transport.ConsultarTimbreAsync(request, ct);

        public Task<TimbreFcResponse> ValidarTimbreFcAsync(TimbreFcRequest request, CancellationToken ct = default) =>
            _transport.ConsultarTimbreFcAsync(request, ct);

        public Task<List<DirectorioContribuyente>> ConsultarDirectorioAsync(CancellationToken ct = default) =>
            _transport.ConsultarDirectorioAsync(ct);

        public Task<List<EstatusServicio>> ConsultarEstatusServiciosAsync(CancellationToken ct = default) =>
            _transport.ConsultarEstatusServiciosAsync(ct);

        public Task<List<VentanaMantenimiento>> ConsultarVentanasMantenimientoAsync(CancellationToken ct = default) =>
            _transport.ConsultarVentanasMantenimientoAsync(ct);

        public Task<string> VerificarEstadoAmbienteAsync(AmbienteEnum ambiente, CancellationToken ct = default) =>
            _transport.VerificarEstadoAmbienteAsync(ambiente, ct);

        public Task<AnulacionResponse> AnularRangosAsync(string xmlContent, CancellationToken ct = default) =>
            _transport.AnularRangosAsync(xmlContent, ct);
    }
}
