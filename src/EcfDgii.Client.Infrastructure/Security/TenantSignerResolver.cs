using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Infrastructure.Security
{
    public class TenantSignerResolver : ITenantSignerResolver
    {
        private readonly string? _connectionString;
        private readonly IEcfXmlSigner _defaultSigner;
        private readonly ILogger<TenantSignerResolver> _logger;

        // Cache signers by normalized RNC with expiration to avoid re-reading DB and parsing PFX on every request
        private readonly ConcurrentDictionary<string, (IEcfXmlSigner Signer, DateTime ExpiresAt)> _cache = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public TenantSignerResolver(
            IConfiguration configuration,
            IEcfXmlSigner defaultSigner,
            ILogger<TenantSignerResolver> logger)
        {
            _defaultSigner = defaultSigner;
            _logger = logger;

            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? configuration["ConnectionStrings__DefaultConnection"]
                ?? configuration["DATABASE_URL"]
                ?? configuration["DB_CONNECTION_STRING"];
        }

        public async Task<IEcfXmlSigner> ResolveSignerAsync(string? rnc, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rnc))
            {
                return _defaultSigner;
            }

            var cleanRnc = Regex.Replace(rnc, @"[^\d]", "").Trim();
            if (string.IsNullOrEmpty(cleanRnc))
            {
                return _defaultSigner;
            }

            // 1. Revisar caché en memoria
            if (_cache.TryGetValue(cleanRnc, out var cached) && cached.ExpiresAt > DateTime.UtcNow)
            {
                return cached.Signer;
            }

            // 2. Intentar cargar desde la tabla "Tenants" en PostgreSQL (ecf_db)
            if (!string.IsNullOrWhiteSpace(_connectionString) &&
                !_connectionString.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await using var conn = new NpgsqlConnection(_connectionString);
                    await conn.OpenAsync(ct);

                    const string query = @"
                        SELECT ""Code"", ""CompanyName"", ""CertificateRawData"", ""CertificatePasswordEncrypted""
                        FROM ""Tenants""
                        WHERE REPLACE(REPLACE(""Rnc"", '-', ''), ' ', '') = @rnc
                          AND ""IsActive"" = true
                        LIMIT 1;";

                    await using var cmd = new NpgsqlCommand(query, conn);
                    cmd.Parameters.AddWithValue("rnc", cleanRnc);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        var code = reader["Code"] as string;
                        var companyName = reader["CompanyName"] as string ?? code;
                        var rawData = reader["CertificateRawData"] as byte[];
                        var password = reader["CertificatePasswordEncrypted"] as string ?? string.Empty;

                        if (rawData != null && rawData.Length > 0)
                        {
                            try
                            {
                                var cert = X509CertificateLoader.LoadPkcs12(rawData, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
                                var signer = new EcfXmlSigner(cert);
                                _logger.LogInformation("Certificado digital cargado dinámicamente desde BD para RNC {Rnc} ({CompanyName})", cleanRnc, companyName);
                                _cache[cleanRnc] = (signer, DateTime.UtcNow.Add(CacheDuration));
                                return signer;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Fallo al instanciar certificado digital para Tenant {Code} (RNC {Rnc}) desde BD.", code, cleanRnc);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error consultando tabla Tenants en BD para RNC {Rnc}.", cleanRnc);
                }
            }

            // 3. Revisar archivos en disco (/app/certificates o certificates)
            var certDirs = new[] { "/app/certificates", "certificates" };
            foreach (var dir in certDirs)
            {
                if (Directory.Exists(dir))
                {
                    var pfxByRnc = Path.Combine(dir, $"{cleanRnc}.pfx");
                    if (File.Exists(pfxByRnc))
                    {
                        try
                        {
                            var signer = new EcfXmlSigner(pfxByRnc, "Fy7g6Q9W");
                            _logger.LogInformation("Certificado digital cargado desde archivo {Path} para RNC {Rnc}", pfxByRnc, cleanRnc);
                            _cache[cleanRnc] = (signer, DateTime.UtcNow.Add(CacheDuration));
                            return signer;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error al cargar certificado desde {Path}", pfxByRnc);
                        }
                    }
                }
            }

            // 4. Fallback seguro al firmador por defecto
            _logger.LogInformation("No se encontró certificado específico para RNC {Rnc}. Utilizando firmador por defecto.", cleanRnc);
            return _defaultSigner;
        }
    }
}
