using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Infrastructure.Dgii;
using EcfDgii.Client.Infrastructure.Security;

namespace EcfDgii.Client
{
    public class EcfFrontendClient
    {
        private readonly IEcfTransport _transport;

        public EcfFrontendClient(EcfClientOptions? options = null, IEcfTransport? transport = null)
        {
            if (transport != null)
            {
                _transport = transport;
                return;
            }

            var opts = options ?? new EcfClientOptions();
            var httpClient = new HttpClient();

            if (opts.Mode == IntegrationMode.DgiiDirect)
            {
                var envConfig = EcfEnvironmentConfig.GetConfig((AmbienteEnum)opts.Environment);

                EcfXmlSigner signer = null;
                if (!string.IsNullOrEmpty(opts.CertificatePath))
                    signer = new EcfXmlSigner(opts.CertificatePath, opts.CertificatePassword ?? "");

                if (signer != null && !string.IsNullOrEmpty(opts.RncEmisor))
                {
                    var tokenManager = new EcfTokenManager(httpClient, signer, envConfig, opts.RncEmisor);
                    _transport = new DgiiDirectTransport(httpClient, tokenManager, envConfig);
                }
                else
                {
                    _transport = new DgiiDirectTransport(httpClient, null, envConfig);
                }
            }
            else
            {
                throw new NotSupportedException("Only DgiiDirect mode is supported.");
            }
        }

        public Task<ConsultaEstadoResponse> ConsultarEstadoAsync(string rncEmisor, string eNcf, string rncComprador = null, string codigoSeguridad = null, CancellationToken ct = default) =>
            _transport.ConsultarEstadoAsync(new ConsultaEstadoRequest(rncEmisor, eNcf, rncComprador, codigoSeguridad), ct);

        public Task<RfceConsultaResponse> ConsultarRfceAsync(string rncEmisor, string eNcf, string codigoSeguridad, CancellationToken ct = default) =>
            _transport.ConsultarRfceAsync(rncEmisor, eNcf, codigoSeguridad, ct);

        public Task<TimbreResponse> ValidarTimbreEcfAsync(TimbreEcfRequest request, CancellationToken ct = default) =>
            _transport.ConsultarTimbreAsync(request, ct);

        public Task<TimbreFcResponse> ValidarTimbreFcAsync(TimbreFcRequest request, CancellationToken ct = default) =>
            _transport.ConsultarTimbreFcAsync(request, ct);
    }
}
