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
using EcfDgii.Client.Application.Common.Interfaces;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Infrastructure.Dgii
{
    public class EcfTokenManager : IEcfTokenManager
    {
        private readonly HttpClient _httpClient;
        private readonly IEcfXmlSigner _signer;
        private readonly EcfEnvironmentConfig _config;
        private readonly string _rncEmisor;
        private readonly ICacheService? _cacheService;

        private string? _cachedToken;
        private DateTimeOffset _tokenExpiry;
        private readonly SemaphoreSlim _renewLock = new SemaphoreSlim(1, 1);

        public class CachedEcfToken
        {
            public string Token { get; set; } = string.Empty;
            public DateTimeOffset Expiration { get; set; }
        }

        public EcfTokenManager(
            HttpClient httpClient, 
            IEcfXmlSigner signer, 
            EcfEnvironmentConfig config, 
            string rncEmisor,
            ICacheService? cacheService = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _rncEmisor = rncEmisor ?? throw new ArgumentNullException(nameof(rncEmisor));
            _cacheService = cacheService;
        }

        /// <summary>
        /// Discards the cached token (memory and, if configured, Redis) so the next GetTokenAsync
        /// call is forced to renew — independent of whatever the proactive 5-minute-margin expiry
        /// check would otherwise decide. Exists for reactive 401 handling: when DGII itself rejects a
        /// token mid-window (early revocation, clock skew, DGII-side session invalidation), the
        /// proactive check alone can't detect that — the caller that actually saw the 401 has to say
        /// "this token is bad" explicitly.
        /// </summary>
        public async Task InvalidateAsync(CancellationToken ct = default)
        {
            await _renewLock.WaitAsync(ct);
            try
            {
                _cachedToken = null;
                _tokenExpiry = default;
            }
            finally
            {
                _renewLock.Release();
            }

            if (_cacheService != null)
            {
                await _cacheService.RemoveAsync($"ecf:tokens:{_rncEmisor}", ct);
            }
        }

        public async Task<string> GetTokenAsync(CancellationToken ct = default)
        {
            string cacheKey = $"ecf:tokens:{_rncEmisor}";

            // 1. Check Redis Cache if available
            if (_cacheService != null)
            {
                var cachedTokenObj = await _cacheService.GetAsync<CachedEcfToken>(cacheKey, ct);
                if (cachedTokenObj != null && !string.IsNullOrEmpty(cachedTokenObj.Token) && (cachedTokenObj.Expiration - DateTimeOffset.UtcNow).TotalMinutes > 5)
                {
                    return cachedTokenObj.Token;
                }
            }

            // 2. Check Local Memory Cache
            if (!string.IsNullOrEmpty(_cachedToken) && (_tokenExpiry - DateTimeOffset.UtcNow).TotalMinutes > 5)
            {
                return _cachedToken;
            }

            // 3. Renew Token under Distributed Lock / Local Lock
            string lockKey = $"ecf:tokens:lock:{_rncEmisor}";
            string lockValue = Guid.NewGuid().ToString();
            bool acquiredDistributedLock = false;

            try
            {
                if (_cacheService != null)
                {
                    acquiredDistributedLock = await _cacheService.AcquireLockAsync(lockKey, lockValue, TimeSpan.FromSeconds(30), ct);
                    if (acquiredDistributedLock)
                    {
                        // Double check cache after acquiring lock
                        var cachedTokenObj = await _cacheService.GetAsync<CachedEcfToken>(cacheKey, ct);
                        if (cachedTokenObj != null && !string.IsNullOrEmpty(cachedTokenObj.Token) && (cachedTokenObj.Expiration - DateTimeOffset.UtcNow).TotalMinutes > 5)
                        {
                            return cachedTokenObj.Token;
                        }
                    }
                }

                await _renewLock.WaitAsync(ct);
                try
                {
                    if (!string.IsNullOrEmpty(_cachedToken) && (_tokenExpiry - DateTimeOffset.UtcNow).TotalMinutes > 5)
                    {
                        return _cachedToken;
                    }

                    await RenewTokenAsync(cacheKey, ct);
                    return _cachedToken!;
                }
                finally
                {
                    _renewLock.Release();
                }
            }
            finally
            {
                if (acquiredDistributedLock && _cacheService != null)
                {
                    await _cacheService.ReleaseLockAsync(lockKey, lockValue, ct);
                }
            }
        }

        private async Task RenewTokenAsync(string cacheKey, CancellationToken ct)
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

            var dateFormats = new[]
            {
                "yyyy-MM-ddTHH:mm:ss.fffZ",
                "yyyy-MM-ddTHH:mm:ss.ffZ",
                "yyyy-MM-ddTHH:mm:ss.fZ",
                "yyyy-MM-ddTHH:mm:ssZ",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-ddTHH:mm:ss"
            };

            if (DateTimeOffset.TryParseExact(expiraElement.Value.Trim(), dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expiry))
            {
                _tokenExpiry = expiry;
            }
            else
            {
                _tokenExpiry = DateTimeOffset.UtcNow.AddHours(1);
            }

            // Save to Redis if available
            if (_cacheService != null && !string.IsNullOrEmpty(_cachedToken))
            {
                var timeToLive = _tokenExpiry - DateTimeOffset.UtcNow;
                if (timeToLive > TimeSpan.Zero)
                {
                    await _cacheService.SetAsync(cacheKey, new CachedEcfToken
                    {
                        Token = _cachedToken,
                        Expiration = _tokenExpiry
                    }, timeToLive, ct);
                }
            }
        }
    }
}
