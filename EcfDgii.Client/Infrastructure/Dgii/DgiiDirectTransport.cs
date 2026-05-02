using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Infrastructure.Serialization;
using EcfDgii.Client.Infrastructure.Security;

namespace EcfDgii.Client.Infrastructure.Dgii
{
    public class DgiiDirectTransport : IEcfTransport
    {
        private readonly HttpClient _httpClient;
        private readonly EcfTokenManager _tokenManager;
        private readonly EcfEnvironmentConfig _config;
        private readonly EcfXmlSerializer _xmlSerializer = new EcfXmlSerializer();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public DgiiDirectTransport(HttpClient httpClient, EcfTokenManager tokenManager, EcfEnvironmentConfig config)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task<EcfRecepcionResponse> SendEcfAsync(string xmlContent, string fileName, CancellationToken ct = default)
        {
            var token = await _tokenManager.GetTokenAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.RecepcionUrl}/api/facturaselectronicas");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var content = new MultipartFormDataContent();
            var xmlFile = new StringContent(xmlContent, Encoding.UTF8, "text/xml");
            content.Add(xmlFile, "xml", fileName);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (response.Content.Headers.ContentType?.MediaType == "application/xml" || responseBody.TrimStart().StartsWith("<"))
            {
                return _xmlSerializer.Deserialize<EcfRecepcionResponse>(responseBody);
            }

            return JsonSerializer.Deserialize<EcfRecepcionResponse>(responseBody, JsonOptions)!;
        }

        public async Task<RfceRecepcionResponse> SendRfceAsync(string xmlContent, string fileName, CancellationToken ct = default)
        {
            var token = await _tokenManager.GetTokenAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.RecepcionFcUrl}/api/recepcion/ecf");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var content = new MultipartFormDataContent();
            var xmlFile = new StringContent(xmlContent, Encoding.UTF8, "text/xml");
            content.Add(xmlFile, "xml", fileName);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (response.Content.Headers.ContentType?.MediaType == "application/xml" || responseBody.TrimStart().StartsWith("<"))
            {
                return _xmlSerializer.Deserialize<RfceRecepcionResponse>(responseBody);
            }

            return JsonSerializer.Deserialize<RfceRecepcionResponse>(responseBody, JsonOptions)!;
        }

        public async Task<ConsultaResultadoResponse> ConsultarResultadoAsync(string trackId, CancellationToken ct = default)
        {
            var token = await _tokenManager.GetTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ConsultaResultadoUrl}/api/consultas/estado?trackid={trackId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<ConsultaResultadoResponse>(responseBody, JsonOptions)!;
        }

        public async Task<ConsultaEstadoResponse> ConsultarEstadoAsync(ConsultaEstadoRequest req, CancellationToken ct = default)
        {
            var token = await _tokenManager.GetTokenAsync(ct);
            var url = $"{_config.ConsultaEstadoUrl}/api/consultas/estado?rncemisor={req.RncEmisor}&ncfelectronico={req.ENcf}";
            if (!string.IsNullOrEmpty(req.RncComprador)) url += $"&rnccomprador={req.RncComprador}";
            if (!string.IsNullOrEmpty(req.CodigoSeguridad)) url += $"&codigoseguridad={req.CodigoSeguridad}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<ConsultaEstadoResponse>(responseBody, JsonOptions)!;
        }

        public async Task<List<TrackIdDetalle>> ConsultarTrackIdsAsync(string rncEmisor, string eNcf, CancellationToken ct = default)
        {
            var token = await _tokenManager.GetTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ConsultaTrackIdsUrl}/api/trackids/consulta?rncemisor={rncEmisor}&encf={eNcf}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<List<TrackIdDetalle>>(responseBody, JsonOptions)!;
        }

        public async Task<RfceConsultaResponse> ConsultarRfceAsync(string rncEmisor, string eNcf, string codigoSeguridad, CancellationToken ct = default)
        {
            var token = await _tokenManager.GetTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.ConsultaRfceUrl}/api/Consultas/Consulta?RNC_Emisor={rncEmisor}&ENCF={eNcf}&Cod_Seguridad_eCF={codigoSeguridad}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<RfceConsultaResponse>(responseBody, JsonOptions)!;
        }

        public async Task<AprobacionComercialResponse> SendAprobacionComercialAsync(string xmlContent, string fileName, CancellationToken ct = default)
        {
            var token = await _tokenManager.GetTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.AprobacionComercialUrl}/api/aprobacioncomercial");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var content = new MultipartFormDataContent();
            var xmlFile = new StringContent(xmlContent, Encoding.UTF8, "text/xml");
            content.Add(xmlFile, "xml", fileName);
            request.Content = content;

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<AprobacionComercialResponse>(responseBody, JsonOptions)!;
        }

        public async Task<AnulacionResponse> AnularRangosAsync(string xmlContent, CancellationToken ct = default)
        {
            var token = await _tokenManager.GetTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.AnulacionRangosUrl}/api/operaciones/anularrango");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(xmlContent, Encoding.UTF8, "text/xml");

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<AnulacionResponse>(responseBody, JsonOptions)!;
        }

        public async Task<List<DirectorioContribuyente>> ConsultarDirectorioAsync(CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.DirectorioUrl}/api/consultas/listado");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<List<DirectorioContribuyente>>(responseBody, JsonOptions)!;
        }

        public async Task<DirectorioContribuyente> ConsultarDirectorioPorRncAsync(string rnc, CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.DirectorioUrl}/api/consultas/obtenerdirectorioporrnc?RNC={rnc}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<DirectorioContribuyente>(responseBody, JsonOptions)!;
        }

        public async Task<TimbreResponse> ConsultarTimbreAsync(TimbreEcfRequest req, CancellationToken ct = default)
        {
            var url = EcfSecurityUtils.BuildTimbreUrl(_config.TimbreUrl, req);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<TimbreResponse>(responseBody, JsonOptions)!;
        }

        public async Task<TimbreFcResponse> ConsultarTimbreFcAsync(TimbreFcRequest req, CancellationToken ct = default)
        {
            var url = EcfSecurityUtils.BuildTimbreFcUrl(_config.TimbreFcUrl, req);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<TimbreFcResponse>(responseBody, JsonOptions)!;
        }

        public async Task<List<EstatusServicio>> ConsultarEstatusServiciosAsync(CancellationToken ct = default)
        {
            // Nota: Este servicio requiere API Key según el MD.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.EstatusServiciosUrl}/api/estatusservicios/obtenerestatus");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            // TODO: Agregar API Key si está disponible en opciones.

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<List<EstatusServicio>>(responseBody, JsonOptions)!;
        }

        public async Task<List<VentanaMantenimiento>> ConsultarVentanasMantenimientoAsync(CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.EstatusServiciosUrl}/api/estatusservicios/obtenerventanasmantenimiento");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            return JsonSerializer.Deserialize<List<VentanaMantenimiento>>(responseBody, JsonOptions)!;
        }

        public async Task<string> VerificarEstadoAmbienteAsync(AmbienteEnum ambiente, CancellationToken ct = default)
        {
            int ambienteId = ambiente switch
            {
                AmbienteEnum.PreCertificacion => 1,
                AmbienteEnum.Produccion => 2,
                AmbienteEnum.Certificacion => 3,
                _ => 1
            };

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.EstatusServiciosUrl}/api/estatusservicios/verificarestado?ambiente={ambienteId}");
            var response = await _httpClient.SendAsync(request, ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
    }
}
