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

        private const string PrefixV1 = "enc:v1:";
        private const string PrefixV2 = "enc:v2:";
        private const string PrefixV3 = "enc:v3:";

        private static byte[] GetAssociatedData(string? tenantId)
        {
            var tenantTag = string.IsNullOrWhiteSpace(tenantId) ? "default" : tenantId.Trim().ToLowerInvariant();
            return System.Text.Encoding.UTF8.GetBytes($"tenant:{tenantTag}|field:CertificatePassword|v3");
        }

        private string GetSecret()
        {
            return _configuration?["STORAGE_ENCRYPTION_KEY"]
                ?? _configuration?["Jwt:SecretKey"]
                ?? _configuration?["JWT_SECRET"]
                ?? "DevStorageKeyOnlyForLocalUnitTesting2026AtLeast32Bytes!";
        }

        private byte[] DeriveKeyV2()
        {
            var secret = GetSecret();
            return System.Security.Cryptography.HKDF.DeriveKey(
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Text.Encoding.UTF8.GetBytes(secret),
                32,
                info: System.Text.Encoding.UTF8.GetBytes("ecf:certificate-password:v1"));
        }

        private byte[] DeriveKeyV1()
        {
            var secret = GetSecret();
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            return sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(secret));
        }

        private string DecryptPassword(string? cipherTextOrPlain, string? tenantCode = null)
        {
            if (string.IsNullOrEmpty(cipherTextOrPlain)) return string.Empty;

            bool isV3 = cipherTextOrPlain.StartsWith(PrefixV3);
            bool isV2 = cipherTextOrPlain.StartsWith(PrefixV2);
            bool isV1 = cipherTextOrPlain.StartsWith(PrefixV1);

            if (!isV3 && !isV2 && !isV1)
            {
                return cipherTextOrPlain;
            }

            try
            {
                var prefixLen = isV3 ? PrefixV3.Length : (isV2 ? PrefixV2.Length : PrefixV1.Length);
                var rawBase64 = cipherTextOrPlain.Substring(prefixLen);
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
                var key = (isV3 || isV2) ? DeriveKeyV2() : DeriveKeyV1();

                using var aesGcm = new System.Security.Cryptography.AesGcm(key, tagSize);
                if (isV3)
                {
                    var associatedData = GetAssociatedData(tenantCode);
                    aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes, associatedData);
                }
                else
                {
                    aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);
                }

                return System.Text.Encoding.UTF8.GetString(plainBytes);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to decrypt certificate password for tenant {TenantCode}", tenantCode);
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
                            var password = DecryptPassword(rawPassword, code);

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
