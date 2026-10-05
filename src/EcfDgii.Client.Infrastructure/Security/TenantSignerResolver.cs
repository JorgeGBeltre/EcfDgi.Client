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
using EcfDgii.Client.Domain.Exceptions;

namespace EcfDgii.Client.Infrastructure.Security
{
    public class TenantSignerResolver : ITenantSignerResolver, IDisposable
    {
        private readonly IConfiguration _configuration;
        private readonly string? _connectionString;
        private readonly IEcfXmlSigner _defaultSigner;
        private readonly ILogger<TenantSignerResolver> _logger;

        // Cache signers by normalized RNC with expiration to avoid re-reading DB and parsing PFX on every request
        private readonly ConcurrentDictionary<string, (IEcfXmlSigner Signer, DateTime ExpiresAt)> _cache = new();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private bool _disposed;

        public TenantSignerResolver(
            IConfiguration configuration,
            IEcfXmlSigner defaultSigner,
            ILogger<TenantSignerResolver> logger)
        {
            _configuration = configuration;
            _defaultSigner = defaultSigner;
            _logger = logger;

            _connectionString = configuration.GetConnectionString("DefaultConnection")
                ?? configuration["ConnectionStrings:DefaultConnection"]
                ?? configuration["ConnectionStrings__DefaultConnection"]
                ?? configuration["DATABASE_URL"]
                ?? configuration["DB_CONNECTION_STRING"];
        }

        private string DecryptPassword(string? cipherTextOrPlain)
        {
            if (string.IsNullOrEmpty(cipherTextOrPlain)) return string.Empty;
            if (!cipherTextOrPlain.StartsWith("enc:v1:")) return cipherTextOrPlain;

            try
            {
                var secret = _configuration?["STORAGE_ENCRYPTION_KEY"]
                    ?? "DevStorageKeyOnlyForLocalUnitTesting2026AtLeast32Bytes!";
                using var sha256 = System.Security.Cryptography.SHA256.Create();
                var key = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(secret));

                var rawBase64 = cipherTextOrPlain.Substring("enc:v1:".Length);
                var combined = Convert.FromBase64String(rawBase64);

                var nonceSize = System.Security.Cryptography.AesGcm.NonceByteSizes.MaxSize;
                var tagSize = System.Security.Cryptography.AesGcm.TagByteSizes.MaxSize;
                var cipherLength = combined.Length - nonceSize - tagSize;
                if (cipherLength < 0) return cipherTextOrPlain;

                var nonce = new byte[nonceSize];
                var tag = new byte[tagSize];
                var cipherBytes = new byte[cipherLength];

                Buffer.BlockCopy(combined, 0, nonce, 0, nonceSize);
                Buffer.BlockCopy(combined, nonceSize, tag, 0, tagSize);
                Buffer.BlockCopy(combined, nonceSize + tagSize, cipherBytes, 0, cipherLength);

                var plainBytes = new byte[cipherLength];
                using var aesGcm = new System.Security.Cryptography.AesGcm(key, tagSize);
                aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);

                return System.Text.Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt certificate password from database");
                return cipherTextOrPlain;
            }
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

            // MED-128: Protección contra cache stampede mediante per-key SemaphoreSlim
            var lockObj = _locks.GetOrAdd(cleanRnc, _ => new SemaphoreSlim(1, 1));
            await lockObj.WaitAsync(ct);
            try
            {
                if (_cache.TryGetValue(cleanRnc, out cached) && cached.ExpiresAt > DateTime.UtcNow)
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

                        // MED-128: Comparar columna Rnc directamente sin REPLACE() para aprovechar el índice B-tree
                        var formattedRnc = cleanRnc.Length == 9
                            ? $"{cleanRnc[..1]}-{cleanRnc[1..3]}-{cleanRnc[3..8]}-{cleanRnc[8]}"
                            : (cleanRnc.Length == 11 ? $"{cleanRnc[..3]}-{cleanRnc[3..10]}-{cleanRnc[10]}" : cleanRnc);

                        const string query = @"
                            SELECT ""Code"", ""CompanyName"", ""CertificateRawData"", ""CertificatePasswordEncrypted""
                            FROM ""Tenants""
                            WHERE (""Rnc"" = @rnc OR ""Rnc"" = @formattedRnc)
                              AND ""IsActive"" = true
                            LIMIT 1;";

                        await using var cmd = new NpgsqlCommand(query, conn);
                        cmd.Parameters.AddWithValue("rnc", cleanRnc);
                        cmd.Parameters.AddWithValue("formattedRnc", formattedRnc);

                        await using var reader = await cmd.ExecuteReaderAsync(ct);
                        if (await reader.ReadAsync(ct))
                        {
                            var code = reader["Code"] as string;
                            var companyName = reader["CompanyName"] as string ?? code;
                            var rawData = reader["CertificateRawData"] as byte[];
                            var rawPassword = reader["CertificatePasswordEncrypted"] as string ?? string.Empty;
                            var password = DecryptPassword(rawPassword);

                            if (rawData != null && rawData.Length > 0)
                            {
                                if (rawData.Length > 2 * 1024 * 1024)
                                {
                                    _logger.LogError("El certificado para Tenant {Code} en base de datos excede 2 MB.", code);
                                    throw new EcfSigningException($"El certificado digital para el tenant {code} excede el límite de 2 MB.");
                                }

                                try
                                {
                                    var cert = X509CertificateLoader.LoadPkcs12(rawData, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                                    var now = DateTime.UtcNow;
                                    if (cert.NotBefore > now || cert.NotAfter < now)
                                    {
                                        _logger.LogError("Certificado digital para Tenant {Code} (RNC {Rnc}) está fuera del periodo de validez ({NotBefore} - {NotAfter}).", code, cleanRnc, cert.NotBefore, cert.NotAfter);
                                        cert.Dispose();
                                        throw new EcfSigningException($"El certificado digital para el tenant {code} (RNC {cleanRnc}) ha expirado o aún no es válido.");
                                    }

                                    var signer = new EcfXmlSigner(cert);
                                    if (!signer.ValidateCertificateSn(cleanRnc))
                                    {
                                        _logger.LogError("El certificado para Tenant {Code} no contiene el RNC {Rnc} correspondiente.", code, cleanRnc);
                                        signer.Dispose();
                                        throw new EcfSigningException($"El certificado digital cargado para el tenant {code} no corresponde al RNC {cleanRnc}.");
                                    }

                                    _logger.LogInformation("Certificado digital cargado dinámicamente desde BD para RNC {Rnc} ({CompanyName})", cleanRnc, companyName);
                                    if (_cache.TryRemove(cleanRnc, out var oldSigner))
                                    {
                                        oldSigner.Signer.Dispose();
                                    }
                                    _cache[cleanRnc] = (signer, DateTime.UtcNow.Add(CacheDuration));
                                    return signer;
                                }
                                catch (EcfSigningException)
                                {
                                    throw;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Fallo al instanciar certificado digital para Tenant {Code} (RNC {Rnc}) desde BD.", code, cleanRnc);
                                    throw new EcfSigningException($"No se pudo cargar el certificado digital para el RNC {cleanRnc}: {ex.Message}", ex);
                                }
                            }
                        }
                    }
                    catch (EcfSigningException)
                    {
                        throw;
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
                                var diskPassword = _configuration["EcfClientOptions:CertificatePassword"]
                                    ?? _configuration["CERTIFICATE_PASSWORD"]
                                    ?? string.Empty;
                                var signer = new EcfXmlSigner(pfxByRnc, diskPassword);
                                if (!signer.ValidateCertificateSn(cleanRnc))
                                {
                                    signer.Dispose();
                                    throw new EcfSigningException($"El certificado en disco para el RNC {cleanRnc} no contiene el RNC correspondiente.");
                                }

                                _logger.LogInformation("Certificado digital cargado desde archivo {Path} para RNC {Rnc}", pfxByRnc, cleanRnc);
                                if (_cache.TryRemove(cleanRnc, out var oldDiskSigner))
                                {
                                    oldDiskSigner.Signer.Dispose();
                                }
                                _cache[cleanRnc] = (signer, DateTime.UtcNow.Add(CacheDuration));
                                return signer;
                            }
                            catch (EcfSigningException)
                            {
                                throw;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error al cargar certificado desde {Path}", pfxByRnc);
                                throw new EcfSigningException($"Error al cargar certificado desde disco para RNC {cleanRnc}.", ex);
                            }
                        }
                    }
                }

                // 4. Fallback seguro al firmador por defecto
                _logger.LogInformation("No se encontró certificado específico para RNC {Rnc}. Utilizando firmador por defecto.", cleanRnc);
                return _defaultSigner;
            }
            finally
            {
                lockObj.Release();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (var entry in _cache.Values)
                {
                    entry.Signer.Dispose();
                }
                _cache.Clear();
                foreach (var sem in _locks.Values)
                {
                    sem.Dispose();
                }
                _locks.Clear();
            }
        }
    }
}
