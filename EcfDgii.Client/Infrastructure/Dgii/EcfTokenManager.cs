using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Xml.Serialization;

using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Domain.Exceptions;

namespace EcfDgii.Client.Infrastructure.Dgii
{
    public class EcfTokenManager
    {
        private readonly HttpClient _httpClient;
        private readonly EcfXmlSigner _signer;
        private readonly EcfEnvironmentConfig _config;
        private readonly string _rncEmisor;

        private string _cachedToken;
        private DateTimeOffset _tokenExpiry;
        private readonly SemaphoreSlim _renewLock = new SemaphoreSlim(1, 1);

        public EcfTokenManager(HttpClient httpClient, EcfXmlSigner signer, EcfEnvironmentConfig config, string rncEmisor)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _rncEmisor = rncEmisor ?? throw new ArgumentNullException(nameof(rncEmisor));
        }

        public async Task<string> GetTokenAsync(CancellationToken ct = default)
        {
            if (!string.IsNullOrEmpty(_cachedToken) && (_tokenExpiry - DateTimeOffset.UtcNow).TotalMinutes > 5)
            {
                return _cachedToken;
            }

            await _renewLock.WaitAsync(ct);
            try
            {
                if (!string.IsNullOrEmpty(_cachedToken) && (_tokenExpiry - DateTimeOffset.UtcNow).TotalMinutes > 5)
                {
                    return _cachedToken;
                }

                await RenewTokenAsync(ct);
                return _cachedToken;
            }
            finally
            {
                _renewLock.Release();
            }
        }

        private async Task RenewTokenAsync(CancellationToken ct)
        {
            var semillaXml = await _httpClient.GetStringAsync($"{_config.AutenticacionUrl}/api/autenticacion/semilla", ct);

            var semillaFirmada = _signer.SignXml(semillaXml, _rncEmisor);

            using var content = new MultipartFormDataContent();
            var fileContent = new StringContent(semillaFirmada, Encoding.UTF8, "text/xml");
            content.Add(fileContent, "xml", "semilla.xml");

            var response = await _httpClient.PostAsync($"{_config.AutenticacionUrl}/api/autenticacion/validarsemilla", content, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);

            var doc = XDocument.Parse(responseBody);
            var tokenElement = doc.Root?.Element("token");
            var expiraElement = doc.Root?.Element("expira");

            if (tokenElement == null || expiraElement == null)
                throw new EcfException("Respuesta de autenticación inválida: falta token o fecha de expiración.");

            _cachedToken = tokenElement.Value;

            if (DateTimeOffset.TryParseExact(expiraElement.Value, "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry))
            {
                _tokenExpiry = expiry;
            }
            else
            {
                _tokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
            }
        }
    }
}
