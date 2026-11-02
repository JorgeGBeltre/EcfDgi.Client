using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using EcfDgii.Client.Application.Documents.Dto;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Domain.Exceptions;
using EcfDgii.Client.Infrastructure.Dgii;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Infrastructure.Serialization;
using EcfDgii.Client.Shared.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EcfDgii.Client.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = "UserOrWorker")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("FiscalEmissionLimiter")]
    public class DocumentsController : ControllerBase
    {
        // States where the eNCF was allocated and persisted but nothing has left this process yet
        // (no XML was ever handed to DGII). A crash-and-retry here is unconditionally safe: reapply
        // whatever the caller sends now — even if the source invoice was edited in between — and
        // retry signing/sending under the SAME eNCF. Never allocate a second eNCF for this TxnId.
        private static readonly HashSet<string> NeverTransmittedStates = new(StringComparer.Ordinal)
        {
            "SequenceAllocated",
            "SigningFailed",
            // Both stop the document strictly BEFORE any DGII call, exactly like the two above:
            // SchemaInvalid means the local XSD gate refused it, Unsigned means there was no real
            // certificate to sign with. Leaving them out stranded the eNCF permanently — a document
            // that failed on a builder bug could never be retried once the bug was fixed, because a
            // resubmit of the same TxnId just returned the stale failed state and the only way
            // forward was to spend a fresh number.
            "SchemaInvalid",
            "Unsigned"
        };

        // The specific DB-level guarantee the concurrent-insert recovery below depends on.
        // Any other unique/FK/etc. violation is a real error and must not be treated as a race.
        private const string TenantSourceTxnUniqueConstraint = "uq_ecf_documents_tenant_source_txn";
        private const string RncEmisorEncfUniqueConstraint = "uq_ecf_documents_rnc_emisor_encf";

        private static bool ComputeIsDefaultFallback(string tenantId, CanonicalDocumentDto? dto, string? rawEnv, bool hasTenantHeader)
        {
            return tenantId == "default-tenant"
                && string.IsNullOrWhiteSpace(rawEnv)
                && !hasTenantHeader
                && string.IsNullOrWhiteSpace(dto?.TenantId);
        }

        private static string? ExtractRazonSocialEmisor(string? xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(xml, @"<RazonSocialEmisor>(.*?)</RazonSocialEmisor>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        // DGII's status query does not necessarily reflect a transmission the instant it lands —
        // there is a processing window where the e-CF was received but doesn't show up yet on
        // consultaestado. Trusting "No encontrado" inside that window risks a real fiscal duplicate:
        // we'd resend something DGII already has. This is deliberately conservative; DGII doesn't
        // publish a guaranteed bound, so err on the side of waiting rather than double-submitting.
        private static readonly TimeSpan MinimumUncertainAgeBeforeReconciliation = TimeSpan.FromMinutes(2);

        // DGII's FechaValidationType — see NormalizeFechaDgii.
        private const string DgiiDateFormat = "dd-MM-yyyy";

        // DGII defines exactly three ITBIS buckets, keyed by rate: I1 = 18%, I2 = 16%, I3 = 0%
        // ("gravado a tasa cero", which is NOT the same as exento). A rate outside this map cannot be
        // declared truthfully in an e-CF, so it is rejected rather than folded into the nearest slot.
        private static readonly IReadOnlyDictionary<int, int> DgiiItbisSlots = new Dictionary<int, int>
        {
            [18] = 1,
            [16] = 2,
            [0] = 3,
        };

        private readonly ApplicationDbContext _db;
        private readonly IEcfSequenceManager _sequenceManager;
        private readonly IEcfClient _ecfClient;
        private readonly IEcfXmlSigner _signer;
        private readonly ILogger<DocumentsController> _logger;
        private readonly IClock _clock;
        private readonly string _emisorRnc;
        private readonly string _emisorRazonSocial;
        private readonly string _emisorDireccion;
        private readonly IEcfSchemaValidator _schemaValidator;
        private readonly EcfClientOptions _ecfClientOptions;
        private readonly AmbienteEnum _defaultAmbiente;
        private readonly ITenantSignerResolver? _signerResolver;
        private readonly FluentValidation.IValidator<CanonicalDocumentDto>? _dtoValidator;

        public DocumentsController(
            ApplicationDbContext db,
            IEcfSequenceManager sequenceManager,
            IEcfClient ecfClient,
            IEcfXmlSigner signer,
            ILogger<DocumentsController> logger,
            IClock clock,
            IOptions<EcfEmisorOptions> emisorOptions,
            IEcfSchemaValidator schemaValidator,
            IOptions<EcfClientOptions> ecfClientOptions,
            ITenantSignerResolver? signerResolver = null,
            FluentValidation.IValidator<CanonicalDocumentDto>? dtoValidator = null)
        {
            _db = db;
            _sequenceManager = sequenceManager;
            _ecfClient = ecfClient;
            _signer = signer;
            _logger = logger;
            _clock = clock;
            // Validated present and well-formed at startup (see Program.cs ValidateOnStart); safe
            // to trust unconditionally here.
            _emisorRnc = emisorOptions.Value.Rnc;
            _emisorRazonSocial = emisorOptions.Value.RazonSocial;
            _emisorDireccion = emisorOptions.Value.Direccion;
            _schemaValidator = schemaValidator;
            _ecfClientOptions = ecfClientOptions.Value;
            _signerResolver = signerResolver;
            _dtoValidator = dtoValidator;
            _defaultAmbiente = _ecfClientOptions.Environment switch
            {
                EcfEnvironment.Test => AmbienteEnum.PreCertificacion,
                EcfEnvironment.Cert => AmbienteEnum.Certificacion,
                EcfEnvironment.Prod => AmbienteEnum.Produccion,
                _ => AmbienteEnum.Certificacion
            };
        }

        private bool TryResolveAmbienteEnum(string? rawEnv, out AmbienteEnum ambiente, out string? error)
        {
            if (string.IsNullOrWhiteSpace(rawEnv))
            {
                ambiente = _defaultAmbiente;
                error = null;
                return true;
            }

            var lower = rawEnv.Trim().ToLowerInvariant();
            if (lower == "test" || lower == "testecf" || lower.Contains("precert"))
            {
                ambiente = AmbienteEnum.PreCertificacion;
                error = null;
                return true;
            }
            if (lower == "cert" || lower == "certecf" || lower.Contains("certific") || lower.Contains("homolog"))
            {
                ambiente = AmbienteEnum.Certificacion;
                error = null;
                return true;
            }
            if (lower == "prod" || lower == "ecf" || lower.Contains("producc"))
            {
                ambiente = AmbienteEnum.Produccion;
                error = null;
                return true;
            }
            if (Enum.TryParse<AmbienteEnum>(rawEnv, true, out var parsed))
            {
                ambiente = parsed;
                error = null;
                return true;
            }

            ambiente = _defaultAmbiente;
            error = $"Ambiente '{rawEnv}' no es reconocido. Valores admitidos: TestEcf, CertEcf, Produccion (o PreCertificacion, Certificacion).";
            return false;
        }

        private AmbienteEnum ResolveAmbienteEnum(string? rawEnv)
        {
            if (TryResolveAmbienteEnum(rawEnv, out var parsed, out _))
            {
                return parsed;
            }
            return _defaultAmbiente;
        }

        private async Task<IEcfXmlSigner> ResolveSignerAsync(string tenantId, string rncEmisor, CanonicalCertificateDto? certDto, bool isDefaultFallback = true)
        {
            if (isDefaultFallback)
            {
                return _signer;
            }

            if (certDto != null && !string.IsNullOrWhiteSpace(certDto.CertificateBase64))
            {
                try
                {
                    if (certDto.CertificateBase64.Length > 3 * 1024 * 1024)
                    {
                        throw new EcfSigningException("El tamaño del certificado digital provisto excede el límite máximo permitido de 2 MB.");
                    }
                    var bytes = Convert.FromBase64String(certDto.CertificateBase64);
                    if (bytes.Length > 2 * 1024 * 1024)
                    {
                        throw new EcfSigningException("El tamaño del certificado digital provisto excede el límite máximo permitido de 2 MB.");
                    }
                    var pwd = certDto.Password ?? string.Empty;
                    var cert = X509CertificateLoader.LoadPkcs12(bytes, pwd, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                    var loadedSigner = new EcfXmlSigner(cert);
                    if (!string.IsNullOrWhiteSpace(rncEmisor) && !loadedSigner.ValidateCertificateSn(rncEmisor))
                    {
                        _logger.LogError("Certificado base64 provisto para Tenant {TenantId} no corresponde al RNC emisor {RncEmisor}.", tenantId, rncEmisor);
                        loadedSigner.Dispose();
                        throw new EcfSigningException($"El certificado provisto para el tenant '{tenantId}' no corresponde al RNC emisor '{rncEmisor}'.");
                    }
                    return loadedSigner;
                }
                catch (EcfSigningException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "No se pudo cargar certificado base64 para Tenant {TenantId}.", tenantId);
                    throw new EcfSigningException($"No se pudo cargar el certificado digital provisto para el tenant '{tenantId}'.", ex);
                }
            }

            // Arbitrary CertificatePath from client body is forbidden for security (HIGH-100)

            // Cargar certificado dinámico real desde PostgreSQL (Tenants)
            if (_signerResolver != null && !string.IsNullOrWhiteSpace(rncEmisor))
            {
                try
                {
                    var tenantSigner = await _signerResolver.ResolveSignerAsync(rncEmisor);
                    if (tenantSigner != null && tenantSigner != _signer)
                    {
                        return tenantSigner;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "TenantSignerResolver no resolvió certificado para RNC {Rnc}", rncEmisor);
                }
            }

            var defaultCertDir = "/app/certificates";
            if (Directory.Exists(defaultCertDir))
            {
                var fullBaseDir = Path.GetFullPath(defaultCertDir);
                var safePassword = certDto?.Password ?? _ecfClientOptions.CertificatePassword;

                if (!string.IsNullOrWhiteSpace(safePassword))
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(tenantId, @"^[a-zA-Z0-9_-]{1,100}$"))
                    {
                        var tenantCertFile = Path.GetFullPath(Path.Combine(defaultCertDir, $"{tenantId}.pfx"));
                        if (tenantCertFile.StartsWith(fullBaseDir + Path.DirectorySeparatorChar) && System.IO.File.Exists(tenantCertFile))
                        {
                            try
                            {
                                return new EcfXmlSigner(tenantCertFile, safePassword);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error al cargar certificado en disco para Tenant {TenantId}.", tenantId);
                            }
                        }
                    }

                    if (System.Text.RegularExpressions.Regex.IsMatch(rncEmisor, @"^\d{9,11}$"))
                    {
                        var rncCertFile = Path.GetFullPath(Path.Combine(defaultCertDir, $"{rncEmisor}.pfx"));
                        if (rncCertFile.StartsWith(fullBaseDir + Path.DirectorySeparatorChar) && System.IO.File.Exists(rncCertFile))
                        {
                            try
                            {
                                return new EcfXmlSigner(rncCertFile, safePassword);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error al cargar certificado en disco para RNC {Rnc}.", rncEmisor);
                            }
                        }
                    }
                }
            }

            // Aislamiento Multi-Tenant:
            // Si el tenant no proveyó certificado propio ni existe en disco, se advierte y se recurre
            // al firmador base (_signer), el cual validará rigurosamente el RNC en ValidateCertificateSn durante SignXml.
            if (!isDefaultFallback && rncEmisor != _emisorRnc)
            {
                _logger.LogWarning(
                    "No se proporcionó certificado digital dinámico para el tenant '{TenantId}' (RNC emisor: {RncEmisor}). " +
                    "Se utilizará el firmador base configurado.", tenantId, rncEmisor);
            }

            return _signer;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IEcfClient> _clientCache = new();

        private IEcfClient ResolveEcfClient(string rncEmisor, IEcfXmlSigner signer, AmbienteEnum ambiente, bool isDefaultFallback = true)
        {
            if (isDefaultFallback || (signer == _signer && rncEmisor == _emisorRnc && ambiente == _defaultAmbiente))
            {
                return _ecfClient;
            }

            var cacheKey = $"{rncEmisor}:{(int)ambiente}:{signer.GetHashCode()}";
            return _clientCache.GetOrAdd(cacheKey, _ =>
            {
                var tenantOptions = new EcfClientOptions
                {
                    RncEmisor = rncEmisor,
                    Environment = ambiente switch
                    {
                        AmbienteEnum.PreCertificacion => EcfEnvironment.Test,
                        AmbienteEnum.Certificacion => EcfEnvironment.Cert,
                        AmbienteEnum.Produccion => EcfEnvironment.Prod,
                        _ => EcfEnvironment.Cert
                    },
                    Mode = IntegrationMode.DgiiDirect,
                    ValidateSchemasLocal = _ecfClientOptions.ValidateSchemasLocal,
                    XsdDirectoryPath = _ecfClientOptions.XsdDirectoryPath
                };

                return new EcfClient(tenantOptions, signer: signer, schemaValidator: _schemaValidator);
            });
        }

        private static readonly HashSet<string> ValidTipoComprobantes = new(StringComparer.OrdinalIgnoreCase)
        {
            "E31", "E32", "E33", "E34", "E41", "E43", "E44", "E45", "E46", "E47"
        };

        [HttpPost]
        public async Task<IActionResult> SubmitCanonicalDocument([FromBody] CanonicalDocumentDto dto)
        {
            if (dto == null)
            {
                return BadRequest(new { error = "Request body is required." });
            }

            if (_dtoValidator != null)
            {
                var validationResult = await _dtoValidator.ValidateAsync(dto);
                if (!validationResult.IsValid)
                {
                    return BadRequest(new { error = validationResult.Errors.First().ErrorMessage, details = validationResult.Errors.Select(e => e.ErrorMessage) });
                }
            }

            if (dto.SourceReference == null || string.IsNullOrWhiteSpace(dto.SourceReference.TxnId))
            {
                return BadRequest(new { error = "SourceReference.TxnId is required." });
            }

            if (string.IsNullOrWhiteSpace(dto.TipoComprobante) || !ValidTipoComprobantes.Contains(dto.TipoComprobante))
            {
                return BadRequest(new { error = $"TipoComprobante '{dto.TipoComprobante}' no es soportado. Valores admitidos: E31, E32, E33, E34, E41, E43, E44, E45, E46, E47." });
            }

            if (!string.IsNullOrWhiteSpace(dto.Header?.RncEmisor))
            {
                var cleanEmisor = System.Text.RegularExpressions.Regex.Replace(dto.Header.RncEmisor, @"[^\d]", "");
                if (cleanEmisor.Length is not (9 or 11))
                {
                    return BadRequest(new { error = $"Header.RncEmisor '{dto.Header.RncEmisor}' es inválido. Debe tener exactamente 9 dígitos (RNC) u 11 dígitos (Cédula)." });
                }
            }

            if (!string.IsNullOrWhiteSpace(dto.Header?.FechaEmision))
            {
                if (!DateTime.TryParseExact(dto.Header.FechaEmision.Trim(), AllowedIncomingDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
                    !DateTime.TryParse(dto.Header.FechaEmision.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    return BadRequest(new { error = $"Header.FechaEmision '{dto.Header.FechaEmision}' tiene un formato de fecha inválido. Formato requerido: dd-MM-yyyy." });
                }
            }

            if (!string.IsNullOrWhiteSpace(dto.References?.FechaNcfModificado))
            {
                if (!DateTime.TryParseExact(dto.References.FechaNcfModificado.Trim(), AllowedIncomingDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _) &&
                    !DateTime.TryParse(dto.References.FechaNcfModificado.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    return BadRequest(new { error = $"References.FechaNcfModificado '{dto.References.FechaNcfModificado}' tiene un formato de fecha inválido. Formato requerido: dd-MM-yyyy." });
                }
            }

            var (_, _, preValidationExchangeRate, currencyValidationError) = ResolveCanonicalCurrency(dto.Currency);
            if (currencyValidationError != null)
            {
                return BadRequest(new { error = currencyValidationError });
            }

            // Type-specific required fields per DGII's "Formato Comprobante Fiscal Electrónico (e-CF)
            // V1.0" spec — checked before allocating a sequence, since a document missing these can
            // never be validly built regardless of what eNCF it gets.
            if (string.Equals(dto.TipoComprobante, "E34", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dto.TipoComprobante, "E33", StringComparison.OrdinalIgnoreCase))
            {
                // Tipo 34 (Nota de Crédito) / Tipo 33 (Nota de Débito): InformacionReferencia is obligatorio (1).
                if (string.IsNullOrWhiteSpace(dto.References?.CorrectsENcf))
                {
                    return BadRequest(new { error = $"References.CorrectsENcf (NCFModificado) is required for TipoComprobante {dto.TipoComprobante}." });
                }
                if (dto.References?.CodigoModificacion is null)
                {
                    return BadRequest(new { error = $"References.CodigoModificacion is required for TipoComprobante {dto.TipoComprobante}." });
                }

                if (string.Equals(dto.TipoComprobante, "E34", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "e-CF {ENcf} tipo 34 (Nota de Crédito) para NCFModificado {NcfModificado}: el tope " +
                        "'MontoTotal ≤ MontoTotal del e-CF modificado' NO se verifica localmente antes de " +
                        "enviar a DGII — solo el servidor de DGII lo valida.",
                        dto.SourceReference.TxnId, dto.References.CorrectsENcf);
                }
            }
            else if (dto.References != null &&
                     !string.IsNullOrWhiteSpace(dto.References.CorrectsENcf) &&
                     dto.References.CodigoModificacion != 4)
            {
                return BadRequest(new
                {
                    error = $"References.CodigoModificacion ({dto.References.CodigoModificacion?.ToString() ?? "null"}) no es válido para TipoComprobante {dto.TipoComprobante} (Regla DGII 613). " +
                            "Fuera de Notas de Débito (E33) y Notas de Crédito (E34), InformacionReferencia solo admite CodigoModificacion = 4 (Reemplazo de comprobante emitido en contingencia)."
                });
            }

            if (string.Equals(dto.TipoComprobante, "E31", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(dto.TipoComprobante, "E45", StringComparison.OrdinalIgnoreCase))
            {
                // e-CF 31 (Crédito Fiscal) & e-CF 45 (Gubernamental): RNCComprador is minOccurs="1" in the real XSD.
                if (string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
                {
                    return BadRequest(new
                    {
                        error = $"Header.RncComprador es obligatorio para TipoComprobante {dto.TipoComprobante}. " +
                                "El cliente no tiene RNC/cédula registrado — corrígelo en el ERP, o emítelo como " +
                                "Factura de Consumo (E32), que no lo exige.",
                    });
                }
                var cleanRnc = System.Text.RegularExpressions.Regex.Replace(dto.Header.RncComprador, @"[^\d]", "");
                if (cleanRnc.Length != 9 && cleanRnc.Length != 11)
                {
                    return BadRequest(new
                    {
                        error = $"Header.RncComprador '{dto.Header.RncComprador}' es inválido para {dto.TipoComprobante}. " +
                                "Debe tener exactamente 9 dígitos (RNC) u 11 dígitos (Cédula)."
                    });
                }
            }
            else if (string.Equals(dto.TipoComprobante, "E32", StringComparison.OrdinalIgnoreCase))
            {
                // Tipo 32 (Factura de Consumo): si el monto total es >= DOP$250,000.00 se debe identificar
                // obligatoriamente RNC/Cédula del comprador o Identificador Extranjero (DGII regla de validación).
                var total = Math.Round((dto.Totals?.MontoTotal ?? 0m) * preValidationExchangeRate, 2, MidpointRounding.AwayFromZero);
                if (total >= 250000m)
                {
                    if (string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
                    {
                        return BadRequest(new
                        {
                            error = "Header.RncComprador (o pasaporte/identificación extranjera) es obligatorio para Factura de Consumo (E32) con MontoTotal ≥ RD$250,000.00 (DGII Regla de Validación tipo 32)."
                        });
                    }
                    var cleanRnc = System.Text.RegularExpressions.Regex.Replace(dto.Header.RncComprador, @"[^\d]", "");
                    var hasAlpha = System.Text.RegularExpressions.Regex.IsMatch(dto.Header.RncComprador, @"[A-Za-z]");
                    if (!hasAlpha && cleanRnc.Length != 9 && cleanRnc.Length != 11)
                    {
                        return BadRequest(new
                        {
                            error = $"Header.RncComprador '{dto.Header.RncComprador}' es inválido para E32 ≥ RD$250,000.00. Debe tener 9 dígitos (RNC), 11 dígitos (Cédula), o ser Identificador Extranjero."
                        });
                    }
                }
            }
            else if (string.Equals(dto.TipoComprobante, "E41", StringComparison.OrdinalIgnoreCase))
            {
                // Tipo 41 (Comprobante de Compras): RNCComprador is obligatorio (1) here (the informal vendor).
                if (string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
                {
                    return BadRequest(new { error = "Header.RncComprador is required for TipoComprobante E41 (the informal vendor's RNC/Cédula)." });
                }
                var cleanRnc = System.Text.RegularExpressions.Regex.Replace(dto.Header.RncComprador, @"[^\d]", "");
                if (cleanRnc.Length != 9 && cleanRnc.Length != 11)
                {
                    return BadRequest(new
                    {
                        error = $"Header.RncComprador '{dto.Header.RncComprador}' es inválido para E41. Debe tener 9 dígitos (RNC) u 11 dígitos (Cédula)."
                    });
                }
                if (dto.Retention is null)
                {
                    return BadRequest(new { error = "Retention is required for TipoComprobante E41." });
                }
            }
            else if (string.Equals(dto.TipoComprobante, "E47", StringComparison.OrdinalIgnoreCase))
            {
                // Tipo 47 (Pagos al Exterior): Retención de ISR es obligatoria (1)
                if (dto.Retention is null)
                {
                    return BadRequest(new { error = "Retention is required for TipoComprobante E47." });
                }
            }
            // MED-105 & MED-107: Pre-allocation validation of amounts and lines
            if (dto.Totals != null)
            {
                if (dto.Totals.MontoSubtotal < 0 || dto.Totals.MontoItbis < 0 || dto.Totals.MontoTotal < 0)
                {
                    return BadRequest(new { error = "Los totales del documento (Subtotal, ITBIS, Total) no pueden ser negativos." });
                }

                var effectiveSubtotal = (dto.Totals.MontoGravadoTotal ?? 0m) + (dto.Totals.MontoExento ?? 0m);
                if (effectiveSubtotal == 0m && dto.Totals.MontoSubtotal > 0m)
                {
                    effectiveSubtotal = dto.Totals.MontoSubtotal;
                }
                else if (dto.Totals.MontoSubtotal == 0m && effectiveSubtotal > 0m)
                {
                    dto.Totals.MontoSubtotal = effectiveSubtotal;
                }

                if (effectiveSubtotal > 0m && Math.Abs(dto.Totals.MontoTotal - (effectiveSubtotal + dto.Totals.MontoItbis)) > 0.05m)
                {
                    return BadRequest(new { error = $"Discrepancia en totales: MontoTotal ({dto.Totals.MontoTotal:F2}) debe coincidir con Subtotal ({effectiveSubtotal:F2}) + ITBIS ({dto.Totals.MontoItbis:F2})." });
                }
            }

            if (dto.Lines != null && dto.Lines.Count > 0)
            {
                // MED-107: Línea negativa inicial no puede preceder al ítem que descuenta
                if (dto.Lines[0].Amount < 0 || dto.Lines[0].UnitPrice < 0)
                {
                    return BadRequest(new { error = "Una línea de descuento no puede preceder al ítem que descuenta." });
                }

                var billableLines = dto.Lines.Where(l => !(l.Amount == 0m && l.UnitPrice == 0m)).ToList();
                if (billableLines.Count == 0 && (dto.Totals?.MontoTotal ?? 0m) > 0m)
                {
                    return BadRequest(new { error = "El comprobante no posee líneas facturables con importe mayor a cero." });
                }

                // MED-105: Líneas deben cuadrar con la base gravada + exenta declarada
                var linesNetSum = dto.Lines.Sum(l => l.Amount);
                var declaredBase = dto.Totals != null 
                    ? ((dto.Totals.MontoGravadoTotal ?? 0m) + (dto.Totals.MontoExento ?? 0m) > 0m 
                        ? (dto.Totals.MontoGravadoTotal ?? 0m) + (dto.Totals.MontoExento ?? 0m) 
                        : dto.Totals.MontoSubtotal)
                    : 0m;

                if (declaredBase > 0 && Math.Abs(linesNetSum - declaredBase) > 0.05m)
                {
                    return BadRequest(new { error = $"Discrepancia en sumatoria de líneas: La suma neta de líneas ({linesNetSum:F2}) no coincide con la base imponible declarada ({declaredBase:F2})." });
                }
            }

            // Checked here, with the other pre-allocation guards: a rate DGII has no bucket for can
            // never produce a truthful document, so it must not cost an eNCF to find that out.
            if (dto.Totals?.TaxBuckets is { Count: > 0 } declaredBuckets)
            {
                var unmappable = declaredBuckets
                    .Select(b => b.Rate)
                    .Where(rate => !DgiiItbisSlots.ContainsKey(rate))
                    .Distinct()
                    .ToList();

                if (unmappable.Count > 0)
                {
                    return BadRequest(new
                    {
                        error = $"Tasa(s) de ITBIS sin bucket DGII: {string.Join(", ", unmappable)}. " +
                                $"Solo se admiten {string.Join(", ", DgiiItbisSlots.Keys)} (I1/I2/I3).",
                    });
                }
            }

            var tenantClaim = User.FindFirst("tenant_id")?.Value;
            var clientType = User.FindFirst("client_type")?.Value;
            var allowedRncsClaim = User.FindFirst("allowed_rncs")?.Value;

            if (!string.IsNullOrWhiteSpace(tenantClaim) && tenantClaim != "default-tenant")
            {
                if (Request.Headers.TryGetValue("X-Tenant-Id", out var hHeader) && !string.IsNullOrWhiteSpace(hHeader) && !string.Equals(hHeader.ToString().Trim(), tenantClaim, StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }
                if (!string.IsNullOrWhiteSpace(dto.TenantId) && !string.Equals(dto.TenantId.Trim(), tenantClaim, StringComparison.OrdinalIgnoreCase))
                {
                    return Forbid();
                }
            }

            var tenantId = (!string.IsNullOrWhiteSpace(tenantClaim) && tenantClaim != "default-tenant")
                ? tenantClaim
                : (HttpContext.Items["TenantId"]?.ToString()
                   ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var hTenant) ? hTenant.ToString() : null)
                   ?? dto.TenantId
                   ?? "default-tenant");

            // Enforce worker allowed_rncs claim if configured (HIGH-107)
            if (clientType == "worker" && !string.IsNullOrWhiteSpace(allowedRncsClaim) && allowedRncsClaim != "*")
            {
                var allowedList = allowedRncsClaim.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var effectiveCheckRnc = !string.IsNullOrWhiteSpace(dto.Header?.RncEmisor) ? dto.Header.RncEmisor : _emisorRnc;
                if (!allowedList.Contains(effectiveCheckRnc, StringComparer.OrdinalIgnoreCase))
                {
                    return Forbid();
                }
            }

            var rawEnv = Request.Headers.TryGetValue("X-Environment", out var hEnv) ? hEnv.ToString() : dto.Environment;
            if (!TryResolveAmbienteEnum(rawEnv, out var ambiente, out var envError))
            {
                return BadRequest(new { error = envError });
            }

            var isDefaultFallback = ComputeIsDefaultFallback(tenantId, dto, rawEnv, Request.Headers.ContainsKey("X-Tenant-Id"));
            var sequenceScope = isDefaultFallback ? "default-tenant" : $"{tenantId}:{ambiente}";

            var editSequence = dto.SourceReference.EditSequence ?? string.Empty;

            // Check if document for this TxnId has already been processed in this ambiente
            var existingDoc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SourceTxnId == dto.SourceReference.TxnId && (d.Ambiente == null || d.Ambiente == ambiente.ToString()));

            if (existingDoc != null)
            {
                return await HandleExistingDocumentAsync(existingDoc, dto, editSequence, ambiente, isDefaultFallback);
            }

            // HIGH-016: Pre-validar longitudes de cadenas para no quemar la secuencia e-NCF si SaveChanges falla por MaxLength
            if (tenantId.Length > 100)
            {
                return BadRequest(new { error = $"TenantId excede la longitud máxima permitida (100). Recibido: {tenantId.Length}" });
            }
            if ((dto.SourceReference?.TxnId?.Length ?? 0) > 100)
            {
                return BadRequest(new { error = $"SourceReference.TxnId excede la longitud máxima permitida (100). Recibido: {dto.SourceReference?.TxnId?.Length}" });
            }
            if (editSequence.Length > 50)
            {
                return BadRequest(new { error = $"EditSequence excede la longitud máxima permitida (50). Recibido: {editSequence.Length}" });
            }
            if ((dto.Header?.RncEmisor?.Length ?? 0) > 20)
            {
                return BadRequest(new { error = $"RncEmisor excede la longitud máxima permitida (20). Recibido: {dto.Header?.RncEmisor?.Length}" });
            }
            if ((dto.Header?.RncComprador?.Length ?? 0) > 20)
            {
                return BadRequest(new { error = $"RncComprador excede la longitud máxima permitida (20). Recibido: {dto.Header?.RncComprador?.Length}" });
            }
            if ((dto.TipoComprobante?.Length ?? 0) > 10)
            {
                return BadRequest(new { error = $"TipoComprobante excede la longitud máxima permitida (10). Recibido: {dto.TipoComprobante?.Length}" });
            }

            // 1. Allocate eNCF Sequence
            string eNcf;
            try
            {
                eNcf = await _sequenceManager.GetNextEncfAsync(sequenceScope, dto.TipoComprobante ?? "E31");
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            var doc = new EcfDocument
            {
                TenantId = tenantId,
                SourceTxnId = dto.SourceReference.TxnId,
                ENcf = eNcf,
                Ambiente = ambiente.ToString()
            };
            ApplyCanonicalContent(doc, dto, editSequence, isDefaultFallback);

            _db.EcfDocuments.Add(doc);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsTenantTxnUniqueViolation(ex))
            {
                // Another request for the same (TenantId, SourceTxnId, Ambiente) won the race.
                // Detach our losing attempt and defer to the winner.
                _db.Entry(doc).State = EntityState.Detached;

                var winner = await _db.EcfDocuments
                    .FirstOrDefaultAsync(d => d.TenantId == tenantId
                                           && d.SourceTxnId == dto.SourceReference.TxnId
                                           && (d.Ambiente == null || d.Ambiente == ambiente.ToString()));
                if (winner == null)
                {
                    _logger.LogError(ex,
                        "Unique constraint violated during e-CF insert for TenantId={TenantId} TxnId={TxnId}, but winner not found.",
                        tenantId, dto.SourceReference.TxnId);
                    throw new InvalidOperationException(
                        $"Unique constraint '{TenantSourceTxnUniqueConstraint}' was violated for TenantId={tenantId} TxnId={dto.SourceReference.TxnId}, " +
                        "but no matching document was found in the database. Concurrent transaction state may be inconsistent.",
                        ex);
                }

                return await HandleExistingDocumentAsync(winner, dto, editSequence, ambiente, isDefaultFallback);
            }

            return await SignAndSendAsync(doc, dto, ambiente, isDefaultFallback);
        }

        private static bool IsTenantTxnUniqueViolation(DbUpdateException ex) =>
            ex.InnerException is PostgresException { SqlState: "23505" } pg
            && (pg.ConstraintName == TenantSourceTxnUniqueConstraint
                || pg.ConstraintName?.Contains("tenant_source_txn", StringComparison.OrdinalIgnoreCase) == true);

        /// <summary>
        /// Decides what to do with a document that already exists for this TxnId, based on how far
        /// the prior attempt got. Shared by the normal lookup path and by the concurrent-insert
        /// recovery path above, so both go through identical never-transmitted/uncertain/terminal logic.
        /// </summary>
        private async Task<IActionResult> HandleExistingDocumentAsync(EcfDocument existingDoc, CanonicalDocumentDto dto, string editSequence, AmbienteEnum? ambiente = null, bool? isDefaultFallbackParam = null)
        {
            var isDefaultFallback = isDefaultFallbackParam ?? ComputeIsDefaultFallback(existingDoc.TenantId, dto, dto.Environment, false);
            if (NeverTransmittedStates.Contains(existingDoc.State))
            {
                // Nothing was ever handed to DGII for this eNCF. Safe to reapply the incoming
                // content — whether or not it changed — and retry under the same eNCF.
                ApplyCanonicalContent(existingDoc, dto, editSequence, isDefaultFallback);
                await _db.SaveChangesAsync();
                return await SignAndSendAsync(existingDoc, dto, ambiente, isDefaultFallback);
            }

            if (existingDoc.State == "Uncertain")
            {
                // We don't know if the prior attempt reached DGII. Ask before doing anything else.
                return await ReconcileUncertainAsync(existingDoc, dto, editSequence, ambiente, isDefaultFallback);
            }

            if (existingDoc.State == "RejectedByDgii")
            {
                // El documento previo fue rechazado por la DGII. Conforme a la normativa de la DGII (Ley 32-23),
                // el eNCF rechazado quedó consumido/inutilizado en los servidores de la DGII (Error 1209 si se reusa).
                // Para reemitir la factura, se debe asignar un NUEVO correlativo de secuencia libre del rango autorizado.
                var tenantId = HttpContext.Items["TenantId"]?.ToString()
                    ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var hTenant) ? hTenant.ToString() : null)
                    ?? dto.TenantId
                    ?? existingDoc.TenantId;

                var rawEnv = Request.Headers.TryGetValue("X-Environment", out var hEnv) ? hEnv.ToString() : dto.Environment;
                var effectiveAmbiente = ambiente ?? ResolveAmbienteEnum(rawEnv);
                var isFallback = isDefaultFallbackParam ?? ComputeIsDefaultFallback(tenantId, dto, rawEnv, Request.Headers.ContainsKey("X-Tenant-Id"));
                var sequenceScope = isFallback ? "default-tenant" : $"{tenantId}:{effectiveAmbiente}";
                string newEncf;
                try
                {
                    newEncf = await _sequenceManager.GetNextEncfAsync(sequenceScope, dto.TipoComprobante ?? "E31");
                }
                catch (InvalidOperationException ex)
                {
                    return BadRequest(new { error = ex.Message });
                }
                _logger.LogInformation("Reemisión de e-CF previamente rechazado para TxnId {TxnId}: eNCF anterior '{OldEncf}' -> Nuevo eNCF '{NewEncf}'",
                    dto.SourceReference.TxnId, existingDoc.ENcf, newEncf);

                existingDoc.ENcf = newEncf;
                existingDoc.TrackId = null;
                existingDoc.SecurityCode = null;
                existingDoc.DgiiResponseXml = null;
                existingDoc.SentToDgiiAt = null;
                existingDoc.State = "AwaitingTransmission";

                ApplyCanonicalContent(existingDoc, dto, editSequence, isFallback);
                await _db.SaveChangesAsync();

                return await SignAndSendAsync(existingDoc, dto, effectiveAmbiente, isFallback);
            }

            // Terminal / known-transmitted states (AwaitingTransmission, Signed, RejectedByDgii, ...).
            if (!string.Equals(existingDoc.EditSequence, editSequence, StringComparison.Ordinal))
            {
                // The source invoice changed after an e-CF was already issued for it. Silently
                // returning the stale document would report wrong amounts as "processed"; this
                // must go through an explicit correction flow (Nota de Débito/Crédito) instead.
                return Conflict(new
                {
                    error = "SourceReference.EditSequence differs from the version already processed for this TxnId. " +
                            "The invoice was modified after its e-CF was issued; issue a correction document instead of resubmitting.",
                    documentId = existingDoc.Id,
                    eNcf = existingDoc.ENcf,
                    state = existingDoc.State,
                    previousEditSequence = existingDoc.EditSequence,
                    incomingEditSequence = editSequence
                });
            }

            return Accepted(new
            {
                documentId = existingDoc.Id,
                eNcf = existingDoc.ENcf,
                state = existingDoc.State,
                trackId = existingDoc.TrackId,
                securityCode = existingDoc.SecurityCode,
                totalAmount = existingDoc.TotalAmount,
                itbisAmount = existingDoc.ItbisAmount,
                signedXml = existingDoc.SignedXmlContent
            });
        }

        /// <summary>
        /// Applies the canonical DTO's content (header, totals, rebuilt XML) onto a document whose
        /// eNCF is already fixed. Used both for a brand-new document and for retrying a document
        /// that never actually reached DGII, where the incoming content may have been edited since.
        ///
        /// In multi-tenant environments, RncEmisor and RazonSocialEmisor come from dto.Header.
        /// In legacy single-tenant/fallback test mode, configured EcfEmisorOptions are respected.
        /// </summary>
        private void ApplyCanonicalContent(EcfDocument doc, CanonicalDocumentDto dto, string editSequence, bool isDefaultFallback = true)
        {
            doc.EditSequence = editSequence;
            doc.DocumentKind = dto.DocumentKind ?? "Invoice";
            doc.Ncf = dto.Ncf;

            var effectiveRnc = !isDefaultFallback && !string.IsNullOrWhiteSpace(dto.Header?.RncEmisor)
                ? dto.Header.RncEmisor
                : _emisorRnc;

            var effectiveRazonSocial = !isDefaultFallback && !string.IsNullOrWhiteSpace(dto.Header?.RazonSocialEmisor)
                ? dto.Header.RazonSocialEmisor
                : _emisorRazonSocial;

            var effectiveDireccion = !isDefaultFallback && !string.IsNullOrWhiteSpace(dto.Header?.DireccionEmisor)
                ? dto.Header.DireccionEmisor
                : (!string.IsNullOrWhiteSpace(_emisorDireccion) ? _emisorDireccion : "Distrito Nacional, SD");

            doc.RncEmisor = effectiveRnc;
            doc.RncComprador = dto.Header?.RncComprador;
            doc.XmlContent = BuildXmlFromCanonical(dto, doc.ENcf, effectiveRnc, effectiveRazonSocial, effectiveDireccion, out var computedTotalDop, out var computedItbisDop);
            doc.TotalAmount = computedTotalDop;
            doc.ItbisAmount = computedItbisDop;
            doc.State = "SequenceAllocated";
        }

        /// <summary>
        /// A prior attempt threw while transmitting to DGII, so it's unknown whether DGII actually
        /// received it. Ask DGII directly instead of guessing: resending blindly risks a duplicate
        /// e-CF under the same eNCF, and silently giving up risks losing one DGII never got.
        /// </summary>
        private async Task<IActionResult> ReconcileUncertainAsync(EcfDocument doc, CanonicalDocumentDto dto, string editSequence, AmbienteEnum? ambiente = null, bool? isDefaultFallbackParam = null)
        {
            var uncertainSince = doc.UpdatedAt.HasValue
                ? new DateTimeOffset(doc.UpdatedAt.Value, TimeSpan.Zero)
                : new DateTimeOffset(doc.CreatedAt, TimeSpan.Zero);

            if (_clock.UtcNow - uncertainSince < MinimumUncertainAgeBeforeReconciliation)
            {
                // Too soon to trust a "No encontrado" from DGII. Report the uncertain state as-is
                // and let the caller retry later — do not even ask DGII yet.
                return Accepted(new
                {
                    documentId = doc.Id,
                    eNcf = doc.ENcf,
                    state = doc.State,
                    trackId = doc.TrackId,
                    securityCode = doc.SecurityCode
                });
            }

            var effectiveAmbiente = ambiente ?? _defaultAmbiente;
            var isDefaultFallback = isDefaultFallbackParam ?? ComputeIsDefaultFallback(doc.TenantId, dto, dto.Environment, false);
            var effectiveSigner = await ResolveSignerAsync(doc.TenantId, doc.RncEmisor, dto.Certificate, isDefaultFallback);
            var effectiveClient = ResolveEcfClient(doc.RncEmisor, effectiveSigner, effectiveAmbiente, isDefaultFallback);

            ConsultaEstadoResponse? status;
            try
            {
                status = await effectiveClient.ConsultarEstadoAsync(doc.RncEmisor, doc.ENcf);
            }
            catch (Exception)
            {
                // Still can't tell. Report the uncertain state as-is rather than guessing either way.
                return Accepted(new
                {
                    documentId = doc.Id,
                    eNcf = doc.ENcf,
                    state = doc.State,
                    trackId = doc.TrackId,
                    securityCode = doc.SecurityCode
                });
            }

            if (status != null && !IsNotFoundByDgii(status))
            {
                // DGII already has it: reconcile local state and do not transmit a duplicate.
                doc.State = "Signed";
                await _db.SaveChangesAsync();
                return Accepted(new
                {
                    documentId = doc.Id,
                    eNcf = doc.ENcf,
                    state = doc.State,
                    trackId = doc.TrackId,
                    securityCode = doc.SecurityCode
                });
            }

            // DGII confirms it never received the prior attempt: safe to treat as never-transmitted.
            ApplyCanonicalContent(doc, dto, editSequence, isDefaultFallback);
            await _db.SaveChangesAsync();
            return await SignAndSendAsync(doc, dto, ambiente, isDefaultFallback);
        }

        private static bool IsNotFoundByDgii(ConsultaEstadoResponse status) =>
            string.Equals(status.Estado?.Trim(), "No encontrado", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Signs and transmits a document whose eNCF is already allocated and persisted
        /// (either just-allocated, or an existing document being retried). Safe to call
        /// repeatedly for the same document: it never touches sequence allocation.
        /// </summary>
        private async Task<IActionResult> SignAndSendAsync(EcfDocument doc, CanonicalDocumentDto? dto = null, AmbienteEnum? ambiente = null, bool? isDefaultFallbackParam = null)
        {
            var effectiveAmbiente = ambiente ?? _defaultAmbiente;
            var isDefaultFallback = isDefaultFallbackParam ?? ComputeIsDefaultFallback(doc.TenantId, dto, dto?.Environment, false);
            var effectiveSigner = await ResolveSignerAsync(doc.TenantId, doc.RncEmisor, dto?.Certificate, isDefaultFallback);
            var effectiveClient = ResolveEcfClient(doc.RncEmisor, effectiveSigner, effectiveAmbiente, isDefaultFallback);

            // 3. Digital signing & Real Security Code Calculation
            string signedXml;
            try
            {
                signedXml = effectiveSigner.SignXml(doc.XmlContent, doc.RncEmisor);
                var secCode = EcfSecurityUtils.CalcularCodigoSeguridad(signedXml);

                doc.SignedXmlContent = signedXml;
                doc.SecurityCode = secCode;
                // NOT "Signed": a locally-applied signature proves nothing until DGII accepts it —
                // that is what "Signed" now means (see below). This is the transient in between, and
                // it deliberately stays OUT of NeverTransmittedStates: if the process dies here we
                // cannot tell whether the DGII call had already gone out.
                doc.State = "AwaitingTransmission";
            }
            catch (Exception ex)
            {
                doc.State = "SigningFailed";
                doc.SignedXmlContent = doc.XmlContent;
                await _db.SaveChangesAsync();
                return BadRequest(new { error = $"Error al firmar digitalmente el XML de e-CF: {ex.Message}" });
            }

            // Local XSD gate — opt-in (ValidateSchemasLocal + XsdDirectoryPath configured, same
            // switches EcfClient.SendEcfAsync's own pre-existing check reads, but exercised HERE too
            // so an invalid document never even reaches the DGII round-trip). Deliberately validates
            // the SIGNED xml, not the pre-signature one: the real DGII XSDs end every e-CF's sequence
            // with a required `xs:any` slot for the ds:Signature XMLDSig injects — a pre-signature
            // document is structurally incomplete by design and would always fail this check for a
            // reason that has nothing to do with the actual content. Signing is a cheap, local
            // operation; only the DGII round-trip is worth avoiding for a document that can't pass.
            if (_ecfClientOptions.ValidateSchemasLocal && !string.IsNullOrEmpty(_ecfClientOptions.XsdDirectoryPath))
            {
                var xsdFileName = EcfXsdFileNameResolver.Resolve(signedXml);
                if (!string.IsNullOrEmpty(xsdFileName))
                {
                    var xsdPath = Path.Combine(_ecfClientOptions.XsdDirectoryPath, xsdFileName);
                    var xsdResult = _schemaValidator.Validate(signedXml, xsdPath);
                    if (!xsdResult.IsValid)
                    {
                        doc.State = "SchemaInvalid";
                        await _db.SaveChangesAsync();
                        _logger.LogError(
                            "e-CF {ENcf} (TxnId {TxnId}) falló validación XSD local (firmado, antes de enviar a DGII): {Errors}",
                            doc.ENcf, doc.SourceTxnId, string.Join(" | ", xsdResult.Errors));
                        return BadRequest(new
                        {
                            error = "El XML firmado no es válido contra el esquema DGII (validación local, antes de enviar a DGII).",
                            details = xsdResult.Errors,
                        });
                    }
                }
            }

            // No DGII-issued credential: EcfXmlSigner self-generated one so local work can proceed,
            // but the result can never be a valid e-CF. Say that plainly instead of transmitting and
            // letting the failure come back as "Uncertain" — which means "we don't know whether DGII
            // received it" and would be a lie here: we know exactly why this cannot succeed.
            // The signed XML is kept so it can still be inspected.
            if (effectiveSigner.UsesFallbackCertificate)
            {
                doc.State = "Unsigned";
                await _db.SaveChangesAsync();
                _logger.LogError(
                    "e-CF {ENcf} (TxnId {TxnId}) NO se transmitió: no hay certificado digital real configurado " +
                    "(se usó uno autogenerado). Estado 'Unsigned'.",
                    doc.ENcf, doc.SourceTxnId);
                return Accepted(new
                {
                    documentId = doc.Id,
                    eNcf = doc.ENcf,
                    state = doc.State,
                    trackId = doc.TrackId,
                    securityCode = doc.SecurityCode,
                    signedXml = doc.SignedXmlContent
                });
            }

            await _db.SaveChangesAsync();

            try
            {
                var isRfce = doc.ENcf.StartsWith("E32", StringComparison.OrdinalIgnoreCase) && doc.TotalAmount < 250000m;
                if (isRfce)
                {
                    var emisorRazon = !isDefaultFallback && !string.IsNullOrWhiteSpace(dto?.Header?.RazonSocialEmisor)
                        ? dto.Header.RazonSocialEmisor
                        : (ExtractRazonSocialEmisor(doc.XmlContent) ?? _emisorRazonSocial);

                    var rfceXml = BuildRfceXml(doc, dto, emisorRazon);
                    var signedRfce = effectiveSigner.SignXml(rfceXml, doc.RncEmisor);

                    doc.SignedRfceContent = signedRfce;
                    // Note: doc.SecurityCode was already assigned from the signed e-CF (signedXml)
                    // and embedded as <CodigoSeguridadeCF> in the RFCE.
                    // DO NOT overwrite doc.SecurityCode with the RFCE summary envelope signature hash,
                    // as DGII's consultatimbrefc endpoint validates against the e-CF's CodigoSeguridadeCF.

                    if (_ecfClientOptions.ValidateSchemasLocal && !string.IsNullOrEmpty(_ecfClientOptions.XsdDirectoryPath))
                    {
                        var xsdFileName = EcfXsdFileNameResolver.Resolve(signedRfce);
                        if (!string.IsNullOrEmpty(xsdFileName))
                        {
                            var xsdPath = Path.Combine(_ecfClientOptions.XsdDirectoryPath, xsdFileName);
                            var xsdResult = _schemaValidator.Validate(signedRfce, xsdPath);
                            if (!xsdResult.IsValid)
                            {
                                doc.State = "SchemaInvalid";
                                await _db.SaveChangesAsync();
                                _logger.LogError(
                                    "RFCE {ENcf} (TxnId {TxnId}) falló validación XSD local: {Errors}",
                                    doc.ENcf, doc.SourceTxnId, string.Join(" | ", xsdResult.Errors));
                                return BadRequest(new
                                {
                                    error = "El XML RFCE firmado no es válido contra el esquema DGII (validación local).",
                                    details = xsdResult.Errors,
                                });
                            }
                        }
                    }

                    var rfceFileName = $"{doc.RncEmisor}{doc.ENcf}.xml";
                    var rfceResp = await effectiveClient.SendRfceAsync(signedRfce, rfceFileName);
                    if (rfceResp != null && (rfceResp.Codigo == 1 || string.Equals(rfceResp.Estado?.Trim(), "Aceptado", StringComparison.OrdinalIgnoreCase)))
                    {
                        doc.TrackId = null; // RecepcionFC issues no TrackId
                        doc.State = "AcceptedByDgii";
                        doc.SentToDgiiAt = _clock.UtcNow.UtcDateTime;
                        doc.DgiiResponseXml = $"Aceptado por DGII (RecepcionFC): {rfceResp.Estado ?? "Aceptado"}";
                        _logger.LogInformation("e-CF de Consumo {ENcf} transmitido y aceptado por RecepcionFC (RFCE).", doc.ENcf);
                    }
                    else if (rfceResp != null && string.Equals(rfceResp.Estado?.Trim(), "Rechazado", StringComparison.OrdinalIgnoreCase))
                    {
                        doc.State = "RejectedByDgii";
                        doc.TrackId = null;
                        var errors = rfceResp.Mensajes != null && rfceResp.Mensajes.Count > 0
                            ? string.Join("; ", rfceResp.Mensajes.Select(m => $"[{m.Codigo}] {m.Valor}"))
                            : (rfceResp.Estado ?? "Rechazado por RecepcionFC");
                        doc.DgiiResponseXml = errors;
                        _logger.LogWarning("e-CF de Consumo {ENcf} rechazado por RecepcionFC: {Errors}", doc.ENcf, errors);
                    }
                    else
                    {
                        // HIGH-041: Respuesta nula o no concluyente de transporte NO es un veredicto de rechazo DGII; debe ser Uncertain
                        doc.State = "Uncertain";
                        doc.TrackId = null;
                        doc.DgiiResponseXml = rfceResp?.Estado ?? "Respuesta nula o no concluyente de transporte RFCE";
                        _logger.LogWarning("e-CF de Consumo {ENcf} no obtuvo respuesta concluyente de RFCE. Marcado como 'Uncertain'.", doc.ENcf);
                    }
                }
                else
                {
                    var fileName = $"{doc.RncEmisor}{doc.ENcf}.xml";
                    var response = await effectiveClient.SendEcfAsync(doc.SignedXmlContent, fileName);
                    if (response != null && !string.IsNullOrWhiteSpace(response.TrackId))
                    {
                        doc.TrackId = response.TrackId;
                        doc.State = "Signed";
                        doc.SentToDgiiAt = _clock.UtcNow.UtcDateTime;
                    }
                    else if (response != null && (!string.IsNullOrWhiteSpace(response.Error) || (response.Mensaje != null && response.Mensaje.Contains("rechazado", StringComparison.OrdinalIgnoreCase))))
                    {
                        doc.State = "RejectedByDgii";
                        doc.DgiiResponseXml = response.Mensaje ?? response.Error ?? "Rechazado por DGII";
                        _logger.LogWarning("e-CF {ENcf} rechazado por DGII al recibir: {Error}", doc.ENcf, doc.DgiiResponseXml);
                    }
                    else
                    {
                        // MED-103 / HIGH-041: Respuesta nula o sin TrackId sin rechazo explícito de DGII es incierta (Uncertain)
                        doc.State = "Uncertain";
                        doc.DgiiResponseXml = response != null 
                            ? (response.Mensaje ?? response.Error ?? "Respuesta sin TrackId desde transporte DGII")
                            : "Respuesta nula desde transporte DGII";
                        _logger.LogWarning("e-CF {ENcf}: respuesta nula o sin TrackId desde DGII. Marcado como 'Uncertain'.", doc.ENcf);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo transmitiendo e-CF {ENcf} (RNC {Rnc}) a DGII: {Message}", doc.ENcf, doc.RncEmisor, ex.Message);
                doc.State = "Uncertain";
                doc.DgiiResponseXml = $"Fallo de transporte / excepción: {ex.Message}";
            }

            await _db.SaveChangesAsync();

            return Accepted(new
            {
                documentId = doc.Id,
                eNcf = doc.ENcf,
                state = doc.State,
                trackId = doc.TrackId,
                securityCode = ResolveEffectiveSecurityCode(doc.SecurityCode, doc.SignedRfceContent, doc.SignedXmlContent),
                totalAmount = doc.TotalAmount,
                itbisAmount = doc.ItbisAmount,
                signedXml = !string.IsNullOrWhiteSpace(doc.SignedRfceContent) ? doc.SignedRfceContent : doc.SignedXmlContent,
                dgiiResponse = doc.DgiiResponseXml
            });
        }

        /// <summary>
        /// Pure read-only lookup of e-CF document by TxnId, TrackId, or eNCF (MED-017).
        /// Idempotent GET: never mutates database rows or contacts DGII.
        /// </summary>
        [HttpGet("by-source/{txnId}")]
        public async Task<IActionResult> GetBySourceTxnId(string txnId)
        {
            var tenantClaim = User.FindFirst("tenant_id")?.Value;
            var tenantId = (!string.IsNullOrWhiteSpace(tenantClaim) && tenantClaim != "default-tenant")
                ? tenantClaim
                : (HttpContext.Items["TenantId"]?.ToString()
                   ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var h) ? h.ToString() : null)
                   ?? "default-tenant");

            // MED-126: Separate sequential indexed probes instead of three-column OR, with AsNoTracking and projection
            var doc = await _db.EcfDocuments
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.SourceTxnId == txnId)
                .Select(d => new
                {
                    d.Id,
                    d.Ncf,
                    d.ENcf,
                    d.State,
                    d.TrackId,
                    d.SecurityCode,
                    d.ReceiptDate,
                    d.TotalAmount,
                    d.ItbisAmount,
                    d.SignedRfceContent,
                    d.SignedXmlContent,
                    d.DgiiResponseXml
                })
                .FirstOrDefaultAsync();

            if (doc == null)
            {
                doc = await _db.EcfDocuments
                    .AsNoTracking()
                    .Where(d => d.TenantId == tenantId && d.TrackId == txnId)
                    .Select(d => new
                    {
                        d.Id,
                        d.Ncf,
                        d.ENcf,
                        d.State,
                        d.TrackId,
                        d.SecurityCode,
                        d.ReceiptDate,
                        d.TotalAmount,
                        d.ItbisAmount,
                        d.SignedRfceContent,
                        d.SignedXmlContent,
                        d.DgiiResponseXml
                    })
                    .FirstOrDefaultAsync();
            }

            if (doc == null)
            {
                doc = await _db.EcfDocuments
                    .AsNoTracking()
                    .Where(d => d.TenantId == tenantId && d.ENcf == txnId)
                    .Select(d => new
                    {
                        d.Id,
                        d.Ncf,
                        d.ENcf,
                        d.State,
                        d.TrackId,
                        d.SecurityCode,
                        d.ReceiptDate,
                        d.TotalAmount,
                        d.ItbisAmount,
                        d.SignedRfceContent,
                        d.SignedXmlContent,
                        d.DgiiResponseXml
                    })
                    .FirstOrDefaultAsync();
            }

            if (doc == null)
            {
                return NotFound(new { error = $"Document with source TxnId '{txnId}' not found." });
            }

            return Ok(new
            {
                documentId = doc.Id,
                ncf = doc.Ncf,
                eNcf = doc.ENcf,
                state = doc.State,
                trackId = doc.TrackId,
                securityCode = ResolveEffectiveSecurityCode(doc.SecurityCode, doc.SignedRfceContent, doc.SignedXmlContent),
                receiptDate = doc.ReceiptDate,
                totalAmount = doc.TotalAmount,
                itbisAmount = doc.ItbisAmount,
                signedXml = !string.IsNullOrWhiteSpace(doc.SignedRfceContent) ? doc.SignedRfceContent : doc.SignedXmlContent,
                dgiiResponse = doc.DgiiResponseXml,
                dgiiResponseXml = doc.DgiiResponseXml
            });
        }

        /// <summary>
        /// Explicit command endpoint to reconcile or retransmit an Uncertain/Signed document against DGII (MED-017).
        /// </summary>
        [HttpPost("by-source/{txnId}/reconcile")]
        public async Task<IActionResult> ReconcileBySourceTxnId(string txnId)
        {
            var tenantClaim = User.FindFirst("tenant_id")?.Value;
            var tenantId = (!string.IsNullOrWhiteSpace(tenantClaim) && tenantClaim != "default-tenant")
                ? tenantClaim
                : (HttpContext.Items["TenantId"]?.ToString()
                   ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var h) ? h.ToString() : null)
                   ?? "default-tenant");

            var doc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => d.TenantId == tenantId && (d.SourceTxnId == txnId || d.TrackId == txnId || d.ENcf == txnId));

            if (doc == null)
            {
                return NotFound(new { error = $"Document with source TxnId '{txnId}' not found." });
            }

            // Si el comprobante sigue en Signed con TrackId, verificar dinámicamente con DGII
            if (doc.State == "Signed" && !string.IsNullOrWhiteSpace(doc.TrackId))
            {
                try
                {
                    var effectiveSigner = await ResolveSignerAsync(doc.TenantId, doc.RncEmisor, null, isDefaultFallback: false);
                    var ambiente = ResolveAmbienteEnum(doc.Ambiente);
                    var client = ResolveEcfClient(doc.RncEmisor, effectiveSigner, ambiente, isDefaultFallback: false);
                    var resultado = await client.ConsultarResultadoAsync(doc.TrackId);
                    if (resultado != null && !string.IsNullOrWhiteSpace(resultado.Estado))
                    {
                        var estado = resultado.Estado.Trim();
                        if (string.Equals(estado, "Aceptado", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(estado, "Aceptado condicional", StringComparison.OrdinalIgnoreCase))
                        {
                            doc.State = "AcceptedByDgii";
                            doc.DgiiResponseXml = $"Aceptado por DGII: {estado}";
                            await _db.SaveChangesAsync();
                        }
                        else if (string.Equals(estado, "Rechazado", StringComparison.OrdinalIgnoreCase))
                        {
                            doc.State = "RejectedByDgii";
                            var errors = resultado.Mensajes != null && resultado.Mensajes.Count > 0
                                ? string.Join("; ", resultado.Mensajes.Select(m => $"[{m.Codigo}] {m.Valor}"))
                                : "Rechazado por DGII";
                            doc.DgiiResponseXml = errors;
                            await _db.SaveChangesAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error consultando resultado DGII para {TrackId}", doc.TrackId);
                }
            }
            else if (doc.State == "Uncertain")
            {
                try
                {
                    var isDefaultFallback = doc.TenantId == "default-tenant";
                    var effectiveSigner = await ResolveSignerAsync(doc.TenantId, doc.RncEmisor, null, isDefaultFallback);
                    var ambiente = ResolveAmbienteEnum(doc.Ambiente);
                    var client = ResolveEcfClient(doc.RncEmisor, effectiveSigner, ambiente, isDefaultFallback);

                    var isRfce = doc.ENcf.StartsWith("E32", StringComparison.OrdinalIgnoreCase) && doc.TotalAmount < 250000m;
                    if (isRfce)
                    {
                        var uncertainSince = doc.UpdatedAt.HasValue
                            ? new DateTimeOffset(doc.UpdatedAt.Value, TimeSpan.Zero)
                            : new DateTimeOffset(doc.CreatedAt, TimeSpan.Zero);

                        if (_clock.UtcNow - uncertainSince >= MinimumUncertainAgeBeforeReconciliation)
                        {
                            ConsultaEstadoResponse? status = null;
                            try
                            {
                                status = await client.ConsultarEstadoAsync(doc.RncEmisor, doc.ENcf);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error al consultar estado DGII para reconciliación de eNCF {eNCF}.", doc.ENcf);
                            }

                            if (status != null)
                            {
                                var estado = status.Estado?.Trim() ?? string.Empty;
                                if (string.Equals(estado, "Aceptado", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(estado, "Aceptado condicional", StringComparison.OrdinalIgnoreCase) ||
                                    status.Codigo == "1")
                                {
                                    doc.TrackId = null; // RecepcionFC issues no TrackId
                                    doc.State = "AcceptedByDgii";
                                    doc.SentToDgiiAt = _clock.UtcNow.UtcDateTime;
                                    doc.DgiiResponseXml = $"Aceptado por DGII (RecepcionFC): {status.Estado ?? "Aceptado"}";
                                    await _db.SaveChangesAsync();
                                }
                                else if (string.Equals(estado, "Rechazado", StringComparison.OrdinalIgnoreCase))
                                {
                                    doc.State = "RejectedByDgii";
                                    doc.TrackId = null;
                                    doc.DgiiResponseXml = $"Rechazado por DGII (RecepcionFC): {status.Estado}";
                                    await _db.SaveChangesAsync();
                                }
                                else if (IsNotFoundByDgii(status))
                                {
                                    var emisorRazon = ExtractRazonSocialEmisor(doc.XmlContent) ?? _emisorRazonSocial;
                                    var rfceXml = BuildRfceXml(doc, null, emisorRazon);
                                    var signedRfce = effectiveSigner.SignXml(rfceXml, doc.RncEmisor);
                                    doc.SignedRfceContent = signedRfce;
                                    // Preserve doc.SecurityCode as the e-CF invoice security code (CodigoSeguridadeCF)

                                    // Local XSD check for RFCE
                                    if (_ecfClientOptions.ValidateSchemasLocal && !string.IsNullOrEmpty(_ecfClientOptions.XsdDirectoryPath))
                                    {
                                        var xsdFileName = EcfXsdFileNameResolver.Resolve(signedRfce);
                                        if (!string.IsNullOrEmpty(xsdFileName))
                                        {
                                            var xsdPath = Path.Combine(_ecfClientOptions.XsdDirectoryPath, xsdFileName);
                                            var xsdResult = _schemaValidator.Validate(signedRfce, xsdPath);
                                            if (!xsdResult.IsValid)
                                            {
                                                doc.State = "SchemaInvalid";
                                                await _db.SaveChangesAsync();
                                                return BadRequest(new
                                                {
                                                    error = "El XML RFCE firmado no es válido contra el esquema DGII.",
                                                    details = xsdResult.Errors
                                                });
                                            }
                                        }
                                    }

                                    var rfceFileName = $"{doc.RncEmisor}{doc.ENcf}.xml";
                                    var rfceResp = await client.SendRfceAsync(signedRfce, rfceFileName);
                                    if (rfceResp != null && (rfceResp.Codigo == 1 || string.Equals(rfceResp.Estado?.Trim(), "Aceptado", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        doc.TrackId = null;
                                        doc.State = "AcceptedByDgii";
                                        doc.SentToDgiiAt = _clock.UtcNow.UtcDateTime;
                                        doc.DgiiResponseXml = $"Aceptado por DGII (RecepcionFC): {rfceResp.Estado ?? "Aceptado"}";
                                        await _db.SaveChangesAsync();
                                        _logger.LogInformation("e-CF de Consumo {ENcf} retransmitido y aceptado por RecepcionFC (RFCE).", doc.ENcf);
                                    }
                                    else if (rfceResp != null && string.Equals(rfceResp.Estado?.Trim(), "Rechazado", StringComparison.OrdinalIgnoreCase))
                                    {
                                        doc.State = "RejectedByDgii";
                                        doc.TrackId = null;
                                        var errors = rfceResp.Mensajes != null && rfceResp.Mensajes.Count > 0
                                            ? string.Join("; ", rfceResp.Mensajes.Select(m => $"[{m.Codigo}] {m.Valor}"))
                                            : (rfceResp.Estado ?? "Rechazado por RecepcionFC");
                                        doc.DgiiResponseXml = errors;
                                        await _db.SaveChangesAsync();
                                    }
                                    else
                                    {
                                        // MED-103: Respuesta nula o no concluyente de transporte RFCE es incierta (Uncertain)
                                        doc.State = "Uncertain";
                                        doc.TrackId = null;
                                        doc.DgiiResponseXml = rfceResp != null 
                                            ? (rfceResp.Estado ?? "Respuesta sin estado concluyente desde RFCE") 
                                            : "Respuesta nula desde RFCE";
                                        await _db.SaveChangesAsync();
                                    }
                                }
                            }
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(doc.TrackId))
                    {
                        var resultado = await client.ConsultarResultadoAsync(doc.TrackId);
                        if (resultado != null && !string.IsNullOrWhiteSpace(resultado.Estado))
                        {
                            var estado = resultado.Estado.Trim();
                            if (string.Equals(estado, "Aceptado", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(estado, "Aceptado condicional", StringComparison.OrdinalIgnoreCase))
                            {
                                doc.State = "AcceptedByDgii";
                                doc.DgiiResponseXml = $"Aceptado por DGII: {estado}";
                                await _db.SaveChangesAsync();
                            }
                            else if (string.Equals(estado, "Rechazado", StringComparison.OrdinalIgnoreCase))
                            {
                                doc.State = "RejectedByDgii";
                                var errors = resultado.Mensajes != null && resultado.Mensajes.Count > 0
                                    ? string.Join("; ", resultado.Mensajes.Select(m => $"[{m.Codigo}] {m.Valor}"))
                                    : "Rechazado por DGII";
                                doc.DgiiResponseXml = errors;
                                await _db.SaveChangesAsync();
                            }
                        }
                    }
                    else
                    {
                        var uncertainSince = doc.UpdatedAt.HasValue
                            ? new DateTimeOffset(doc.UpdatedAt.Value, TimeSpan.Zero)
                            : new DateTimeOffset(doc.CreatedAt, TimeSpan.Zero);

                        if (_clock.UtcNow - uncertainSince >= MinimumUncertainAgeBeforeReconciliation)
                        {
                            ConsultaEstadoResponse? status = null;
                            try
                            {
                                status = await client.ConsultarEstadoAsync(doc.RncEmisor, doc.ENcf);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error al consultar estado DGII para reconciliación de eNCF {eNCF}.", doc.ENcf);
                            }

                            if (status != null)
                            {
                                var estado = status.Estado?.Trim() ?? string.Empty;
                                if (string.Equals(estado, "Aceptado", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(estado, "Aceptado condicional", StringComparison.OrdinalIgnoreCase) ||
                                    status.Codigo == "1")
                                {
                                    doc.State = "AcceptedByDgii";
                                    doc.SentToDgiiAt = _clock.UtcNow.UtcDateTime;
                                    doc.DgiiResponseXml = $"Aceptado por DGII: {status.Estado ?? "Aceptado"}";
                                    await _db.SaveChangesAsync();
                                }
                                else if (string.Equals(estado, "Rechazado", StringComparison.OrdinalIgnoreCase))
                                {
                                    doc.State = "RejectedByDgii";
                                    doc.DgiiResponseXml = $"Rechazado por DGII: {status.Estado}";
                                    await _db.SaveChangesAsync();
                                }
                                else if (IsNotFoundByDgii(status) && !string.IsNullOrWhiteSpace(doc.XmlContent))
                                {
                                    var fileName = $"{doc.RncEmisor}{doc.ENcf}.xml";
                                    var signedXml = effectiveSigner.SignXml(doc.XmlContent, doc.RncEmisor);
                                    doc.SignedXmlContent = signedXml;
                                    doc.SecurityCode = EcfSecurityUtils.CalcularCodigoSeguridad(signedXml);

                                    var response = await client.SendEcfAsync(signedXml, fileName);
                                    if (response != null && !string.IsNullOrWhiteSpace(response.TrackId))
                                    {
                                        doc.TrackId = response.TrackId;
                                        doc.State = "Signed";
                                        doc.SentToDgiiAt = _clock.UtcNow.UtcDateTime;
                                        doc.DgiiResponseXml = $"Recibido por DGII. TrackId: {response.TrackId}";
                                        await _db.SaveChangesAsync();
                                        _logger.LogInformation("e-CF estándar {ENcf} retransmitido y recibido por DGII. TrackId={TrackId}", doc.ENcf, doc.TrackId);
                                    }
                                    else if (response != null && (!string.IsNullOrWhiteSpace(response.Error) || (response.Mensaje != null && response.Mensaje.Contains("rechazado", StringComparison.OrdinalIgnoreCase))))
                                    {
                                        doc.State = "RejectedByDgii";
                                        doc.DgiiResponseXml = response.Mensaje ?? response.Error ?? "Rechazado por DGII";
                                        await _db.SaveChangesAsync();
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error intentando reconciliar documento Uncertain {ENcf}", doc.ENcf);
                }
            }

            return Ok(new
            {
                documentId = doc.Id,
                ncf = doc.Ncf,
                eNcf = doc.ENcf,
                state = doc.State,
                trackId = doc.TrackId,
                securityCode = ResolveEffectiveSecurityCode(doc.SecurityCode, doc.SignedRfceContent, doc.SignedXmlContent),
                receiptDate = doc.ReceiptDate,
                totalAmount = doc.TotalAmount,
                itbisAmount = doc.ItbisAmount,
                signedXml = !string.IsNullOrWhiteSpace(doc.SignedRfceContent) ? doc.SignedRfceContent : doc.SignedXmlContent,
                dgiiResponse = doc.DgiiResponseXml,
                dgiiResponseXml = doc.DgiiResponseXml
            });
        }

        [HttpGet("by-source/{txnId}/xml")]
        public async Task<IActionResult> GetXmlBySourceTxnId(string txnId)
        {
            var tenantClaim = User.FindFirst("tenant_id")?.Value;
            var tenantId = (!string.IsNullOrWhiteSpace(tenantClaim) && tenantClaim != "default-tenant")
                ? tenantClaim
                : (HttpContext.Items["TenantId"]?.ToString()
                   ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var h) ? h.ToString() : null)
                   ?? "default-tenant");

            // MED-126: Probes in order: (TenantId, SourceTxnId), then TrackId, then ENcf
            var doc = await _db.EcfDocuments
                .AsNoTracking()
                .Where(d => d.TenantId == tenantId && d.SourceTxnId == txnId)
                .Select(d => new { d.RncEmisor, d.ENcf, d.SignedRfceContent, d.SignedXmlContent })
                .FirstOrDefaultAsync();

            if (doc == null)
            {
                doc = await _db.EcfDocuments
                    .AsNoTracking()
                    .Where(d => d.TenantId == tenantId && d.TrackId == txnId)
                    .Select(d => new { d.RncEmisor, d.ENcf, d.SignedRfceContent, d.SignedXmlContent })
                    .FirstOrDefaultAsync();
            }

            if (doc == null)
            {
                doc = await _db.EcfDocuments
                    .AsNoTracking()
                    .Where(d => d.TenantId == tenantId && d.ENcf == txnId)
                    .Select(d => new { d.RncEmisor, d.ENcf, d.SignedRfceContent, d.SignedXmlContent })
                    .FirstOrDefaultAsync();
            }

            if (doc == null || (string.IsNullOrWhiteSpace(doc.SignedXmlContent) && string.IsNullOrWhiteSpace(doc.SignedRfceContent)))
            {
                return NotFound(new { error = $"Document with source TxnId '{txnId}' not found or has no XML." });
            }

            var fileName = $"{doc.RncEmisor}-{doc.ENcf}.xml";
            var xmlToReturn = !string.IsNullOrWhiteSpace(doc.SignedRfceContent) ? doc.SignedRfceContent : doc.SignedXmlContent!;
            return File(Encoding.UTF8.GetBytes(xmlToReturn), "application/xml", fileName);
        }

        [HttpGet("{id:guid}/xml")]
        public async Task<IActionResult> GetXmlById(Guid id)
        {
            var tenantClaim = User.FindFirst("tenant_id")?.Value;
            var tenantId = (!string.IsNullOrWhiteSpace(tenantClaim) && tenantClaim != "default-tenant")
                ? tenantClaim
                : (HttpContext.Items["TenantId"]?.ToString()
                   ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var h) ? h.ToString() : null)
                   ?? "default-tenant");

            var doc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id);

            if (doc == null || (string.IsNullOrWhiteSpace(doc.SignedXmlContent) && string.IsNullOrWhiteSpace(doc.SignedRfceContent)))
            {
                return NotFound(new { error = $"Document '{id}' not found or has no XML." });
            }

            var fileName = $"{doc.RncEmisor}-{doc.ENcf}.xml";
            var xmlToReturn = !string.IsNullOrWhiteSpace(doc.SignedRfceContent) ? doc.SignedRfceContent : doc.SignedXmlContent!;
            return File(Encoding.UTF8.GetBytes(xmlToReturn), "application/xml", fileName);
        }

        private static string? ResolveEffectiveSecurityCode(string? currentCode, string? signedRfce, string? signedXml)
        {
            if (!string.IsNullOrWhiteSpace(signedRfce))
            {
                var match = System.Text.RegularExpressions.Regex.Match(signedRfce, @"<CodigoSeguridadeCF>([^<]+)</CodigoSeguridadeCF>");
                if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
                {
                    return match.Groups[1].Value.Trim();
                }
            }
            return currentCode;
        }

        private string BuildRfceXml(EcfDocument doc, CanonicalDocumentDto? dto, string emisorRazonSocial)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<RFCE>");
            sb.AppendLine("  <Encabezado>");
            sb.AppendLine("    <Version>1.0</Version>");
            sb.AppendLine("    <IdDoc>");
            sb.AppendLine("      <TipoeCF>32</TipoeCF>");
            sb.AppendLine($"      <eNCF>{doc.ENcf}</eNCF>");
            sb.AppendLine("      <TipoIngresos>01</TipoIngresos>");
            sb.AppendLine("      <TipoPago>1</TipoPago>");
            sb.AppendLine("    </IdDoc>");
            sb.AppendLine("    <Emisor>");
            sb.AppendLine($"      <RNCEmisor>{doc.RncEmisor}</RNCEmisor>");
            var safeEmisorName = emisorRazonSocial.Length > 150 ? emisorRazonSocial[..150] : emisorRazonSocial;
            sb.AppendLine($"      <RazonSocialEmisor>{EscapeXml(safeEmisorName)}</RazonSocialEmisor>");
            var fechaEmision = NormalizeFechaDgii(dto?.Header?.FechaEmision);
            sb.AppendLine($"      <FechaEmision>{fechaEmision}</FechaEmision>");
            sb.AppendLine("    </Emisor>");
            sb.AppendLine("    <Comprador>");
            if (!string.IsNullOrWhiteSpace(doc.RncComprador))
            {
                var cleanRnc = System.Text.RegularExpressions.Regex.Replace(doc.RncComprador, @"[^\d]", "");
                if (cleanRnc.Length is 9 or 11)
                {
                    sb.AppendLine($"      <RNCComprador>{cleanRnc}</RNCComprador>");
                }
            }
            var compradorName = !string.IsNullOrWhiteSpace(dto?.Header?.RazonSocialComprador)
                ? dto.Header.RazonSocialComprador
                : "CONSUMIDOR FINAL";
            var safeCompradorName = compradorName.Length > 150 ? compradorName[..150] : compradorName;
            sb.AppendLine($"      <RazonSocialComprador>{EscapeXml(safeCompradorName)}</RazonSocialComprador>");
            sb.AppendLine("    </Comprador>");
            sb.AppendLine("    <Totales>");
            if (doc.ItbisAmount > 0)
            {
                var montoGravado = Math.Max(0m, doc.TotalAmount - doc.ItbisAmount);
                sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoTotal>{montoGravado:F2}</MontoGravadoTotal>");
                sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoI1>{montoGravado:F2}</MontoGravadoI1>");
                sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBIS>{doc.ItbisAmount:F2}</TotalITBIS>");
                sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBIS1>{doc.ItbisAmount:F2}</TotalITBIS1>");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoExento>{doc.TotalAmount:F2}</MontoExento>");
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotal>{doc.TotalAmount:F2}</MontoTotal>");
            sb.AppendLine("    </Totales>");
            sb.AppendLine($"    <CodigoSeguridadeCF>{doc.SecurityCode}</CodigoSeguridadeCF>");
            sb.AppendLine("  </Encabezado>");
            sb.AppendLine("</RFCE>");
            return sb.ToString().Trim();
        }

        private static readonly HashSet<string> ValidDgiiForeignCurrencyCodes = new(StringComparer.OrdinalIgnoreCase)
        {
            "BRL", "CAD", "CHF", "CHY", "XDR", "DKK", "EUR", "GBP",
            "JPY", "NOK", "SCP", "SEK", "USD", "VEF", "HTG", "MXN", "COP"
        };

        private static (bool IsForeign, string Code, decimal ExchangeRate, string? ValidationError) ResolveCanonicalCurrency(CanonicalCurrencyDto? currency)
        {
            if (currency == null || string.IsNullOrWhiteSpace(currency.Code))
            {
                return (false, "DOP", 1m, null);
            }

            var code = currency.Code.Trim().ToUpperInvariant();
            if (code is "DOP" or "RD$" or "RDS")
            {
                return (false, "DOP", 1m, null);
            }

            if (code == "CNY") code = "CHY";
            else if (code == "US$") code = "USD";

            if (!ValidDgiiForeignCurrencyCodes.Contains(code))
            {
                return (true, code, currency.ExchangeRate,
                    $"Currency.Code '{currency.Code}' no es una moneda extranjera válida según el esquema TipoMonedaType de la DGII.");
            }

            if (currency.ExchangeRate <= 0m || currency.ExchangeRate >= 1000m)
            {
                return (true, code, currency.ExchangeRate,
                    $"Currency.ExchangeRate ({currency.ExchangeRate}) debe ser mayor a 0 y menor a 1000 para comprobantes en Otra Moneda.");
            }

            return (true, code, Math.Round(currency.ExchangeRate, 4, MidpointRounding.AwayFromZero), null);
        }

        private static string FormatDecimalUpTo4(decimal value) =>
            Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString("0.00##", CultureInfo.InvariantCulture);

        private string BuildXmlFromCanonical(CanonicalDocumentDto dto, string eNcf, string emisorRnc, string emisorRazonSocial, string? emisorDireccion = null) =>
            BuildXmlFromCanonical(dto, eNcf, emisorRnc, emisorRazonSocial, emisorDireccion, out _, out _);

        private string BuildXmlFromCanonical(CanonicalDocumentDto dto, string eNcf, string emisorRnc, string emisorRazonSocial, string? emisorDireccion, out decimal computedTotalDop, out decimal computedItbisDop)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<ECF>");
            sb.AppendLine("  <Encabezado>");
            sb.AppendLine("    <Version>1.0</Version>");
            sb.AppendLine("    <IdDoc>");
            
            var tipoEcf = "31";
            if (!string.IsNullOrWhiteSpace(dto.TipoComprobante) && dto.TipoComprobante.Length >= 3)
            {
                tipoEcf = dto.TipoComprobante.Substring(1); // "E31" -> "31"
            }
            
            sb.AppendLine($"      <TipoeCF>{EscapeXml(tipoEcf)}</TipoeCF>");
            sb.AppendLine($"      <eNCF>{eNcf}</eNCF>");

            // Verified against the real DGII XSDs (checked into this repo under "Documentación
            // Técnica (XSD)") — the field-level obligatoriedad tables in the prose spec do NOT
            // capture this: FechaVencimientoSecuencia and IndicadorNotaCredito are MUTUALLY
            // EXCLUSIVE in IdDoc's xs:sequence, not both-optional-fields. Tipo 34's IdDoc has no
            // FechaVencimientoSecuencia element at all; every other type this codebase emits (31/41)
            // has no IndicadorNotaCredito element at all.
            // Verified element-by-element against the real XSDs, because the three cases are NOT a
            // 34-vs-everything-else binary the way this originally assumed:
            //   34 → IndicadorNotaCredito, no FechaVencimientoSecuencia
            //   32 → NEITHER element exists in its IdDoc
            //   31/33/41/43/44/45/46/47 → FechaVencimientoSecuencia
            // Emitting FechaVencimientoSecuencia for tipo 32 failed every consumo invoice.
            if (tipoEcf == "34")
            {
                // IndicadorNotaCreditoType (Norma DGII / e-CF 34 XSD):
                // 0 = Si fecha de emisión es <= a 30 días calendario del e-CF afectado (preserva derecho a rebaja de ITBIS).
                // 1 = Si fecha de emisión es > a 30 días calendario del e-CF afectado.
                int indicadorNotaCredito = 0;
                if (dto.References?.IndicadorNotaCredito.HasValue == true)
                {
                    indicadorNotaCredito = dto.References.IndicadorNotaCredito.Value;
                }
                else
                {
                    try
                    {
                        var fechaEmisionStr = NormalizeFechaDgii(dto.Header?.FechaEmision);
                        var rawModDate = !string.IsNullOrWhiteSpace(dto.References?.FechaNcfModificado)
                            ? dto.References.FechaNcfModificado
                            : null;

                        if (rawModDate != null)
                        {
                            var fechaModStr = NormalizeFechaDgii(rawModDate);
                            if (DateTime.TryParseExact(fechaEmisionStr, DgiiDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtEmision) &&
                                DateTime.TryParseExact(fechaModStr, DgiiDateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dtMod))
                            {
                                indicadorNotaCredito = (dtEmision.Date - dtMod.Date).TotalDays > 30 ? 1 : 0;
                            }
                        }
                    }
                    catch
                    {
                        indicadorNotaCredito = 0;
                    }
                }
                sb.AppendLine($"      <IndicadorNotaCredito>{indicadorNotaCredito}</IndicadorNotaCredito>");
            }
            else if (tipoEcf != "32")
            {
                var rawVencimiento = dto.FechaVencimientoSecuencia 
                    ?? dto.Header?.FechaVencimientoSecuencia;

                string fechaVencimiento;
                if (!string.IsNullOrWhiteSpace(rawVencimiento))
                {
                    fechaVencimiento = NormalizeFechaDgii(rawVencimiento);
                }
                else
                {
                    // Si el ambiente es pruebas (PreCertificación/Certificación), la vigencia en DGII es fija a 31-12-2028.
                    // En producción, es dinámicamente según normativa DGII (31 de diciembre del año posterior).
                    var isTestOrCert = string.Equals(dto.Environment, "PreCertificacion", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(dto.Environment, "Certificacion", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(dto.Environment, "Test", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(dto.Environment, "Cert", StringComparison.OrdinalIgnoreCase);
                    var dynamicYear = isTestOrCert ? 2028 : Math.Max(2027, DateTime.UtcNow.Year + 1);
                    fechaVencimiento = $"31-12-{dynamicYear}";
                }

                sb.AppendLine($"      <FechaVencimientoSecuencia>{fechaVencimiento}</FechaVencimientoSecuencia>");
            }

            if (tipoEcf is "31" or "32" or "33" or "34" or "41" or "45")
            {
                // IndicadorMontoGravado: 0 = montos en líneas sección B no tienen ITBIS incluido, 1 = se encuentran con ITBIS incluido.
                // Requerido por la DGII cuando el documento contiene montos gravados (Error 176 si falta).
                sb.AppendLine("      <IndicadorMontoGravado>0</IndicadorMontoGravado>");
            }

            // TipoIngresos doesn't exist as an element at all in tipo 41, 43, 47 IdDoc schemas (confirmed by
            // running the real XSDs). Present for 31, 32, 33, 34, 44, 45, 46.
            if (tipoEcf is not "41" and not "43" and not "47")
            {
                // In 46 (Exportaciones), TipoIngresos 02 = Ingresos por exportaciones (or 01).
                var tipoIngresoVal = tipoEcf == "46" ? "02" : "01";
                sb.AppendLine($"      <TipoIngresos>{tipoIngresoVal}</TipoIngresos>");
            }
            sb.AppendLine("      <TipoPago>1</TipoPago>");
            sb.AppendLine("    </IdDoc>");
            
            sb.AppendLine("    <Emisor>");
            // Deliberately emisorRnc/emisorRazonSocial (this instance's configured identity), never
            // dto.Header?.RncEmisor/RazonSocialEmisor — see ApplyCanonicalContent's doc comment.
            sb.AppendLine($"      <RNCEmisor>{EscapeXml(emisorRnc)}</RNCEmisor>");
            var safeEmisorName = emisorRazonSocial.Length > 150 ? emisorRazonSocial[..150] : emisorRazonSocial;
            var razonSocialEmisor = EscapeXml(safeEmisorName);
            sb.AppendLine($"      <RazonSocialEmisor>{razonSocialEmisor}</RazonSocialEmisor>");
            var safeDireccion = !string.IsNullOrWhiteSpace(emisorDireccion) ? emisorDireccion : "Distrito Nacional, SD";
            sb.AppendLine($"      <DireccionEmisor>{EscapeXml(safeDireccion)}</DireccionEmisor>");
            var fechaEmision = NormalizeFechaDgii(dto.Header?.FechaEmision);
            sb.AppendLine($"      <FechaEmision>{EscapeXml(fechaEmision)}</FechaEmision>");
            sb.AppendLine("    </Emisor>");

            // <Comprador> is omitted entirely for tipo 43 (Gastos Menores) because the schema
            // forbids it. For tipo 47 (Pagos al Exterior), only IdentificadorExtranjero and
            // RazonSocialComprador are allowed. For all other types, Comprador is minOccurs="1".
            if (tipoEcf != "43")
            {
                sb.AppendLine("    <Comprador>");
                if (tipoEcf == "47")
                {
                    if (!string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
                    {
                        var foreignId = dto.Header.RncComprador.Length > 20 ? dto.Header.RncComprador[..20] : dto.Header.RncComprador;
                        sb.AppendLine($"      <IdentificadorExtranjero>{EscapeXml(foreignId)}</IdentificadorExtranjero>");
                    }
                }
                else if (tipoEcf == "46")
                {
                    if (!string.IsNullOrWhiteSpace(dto.Header?.IdentificadorExtranjero))
                    {
                        var foreignId = dto.Header.IdentificadorExtranjero.Trim();
                        if (foreignId.Length > 20) foreignId = foreignId[..20];
                        sb.AppendLine($"      <IdentificadorExtranjero>{EscapeXml(foreignId)}</IdentificadorExtranjero>");
                    }
                    else if (!string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(dto.Header.RncComprador, @"[A-Za-z]"))
                        {
                            var foreignId = dto.Header.RncComprador.Length > 20 ? dto.Header.RncComprador[..20] : dto.Header.RncComprador;
                            sb.AppendLine($"      <IdentificadorExtranjero>{EscapeXml(foreignId)}</IdentificadorExtranjero>");
                        }
                        else
                        {
                            var cleanRnc = System.Text.RegularExpressions.Regex.Replace(dto.Header.RncComprador, @"[^\d]", "");
                            if (cleanRnc.Length is 9 or 11)
                            {
                                sb.AppendLine($"      <RNCComprador>{cleanRnc}</RNCComprador>");
                            }
                            else
                            {
                                var foreignId = dto.Header.RncComprador.Length > 20 ? dto.Header.RncComprador[..20] : dto.Header.RncComprador;
                                sb.AppendLine($"      <IdentificadorExtranjero>{EscapeXml(foreignId)}</IdentificadorExtranjero>");
                            }
                        }
                    }
                    else
                    {
                        // Fallback obligatorio DGII para E46 cuando el cliente extranjero no posee RNC Dominicano
                        sb.AppendLine("      <IdentificadorExtranjero>EXTRANJERO</IdentificadorExtranjero>");
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(dto.Header?.IdentificadorExtranjero) && tipoEcf is "32" or "33" or "34" or "44")
                    {
                        var foreignId = dto.Header.IdentificadorExtranjero.Trim();
                        if (foreignId.Length > 20) foreignId = foreignId[..20];
                        sb.AppendLine($"      <IdentificadorExtranjero>{EscapeXml(foreignId)}</IdentificadorExtranjero>");
                    }
                    else if (!string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
                    {
                        if (System.Text.RegularExpressions.Regex.IsMatch(dto.Header.RncComprador, @"[A-Za-z]") && tipoEcf is "32" or "33" or "34" or "44")
                        {
                            var foreignId = dto.Header.RncComprador.Length > 20 ? dto.Header.RncComprador[..20] : dto.Header.RncComprador;
                            sb.AppendLine($"      <IdentificadorExtranjero>{EscapeXml(foreignId)}</IdentificadorExtranjero>");
                        }
                        else
                        {
                            var cleanRnc = System.Text.RegularExpressions.Regex.Replace(dto.Header.RncComprador, @"[^\d]", "");
                            if (cleanRnc.Length is 9 or 11 || tipoEcf is "31" or "41" or "45")
                            {
                                sb.AppendLine($"      <RNCComprador>{cleanRnc}</RNCComprador>");
                            }
                        }
                    }
                    else if (tipoEcf is "33" or "34" && (dto.References?.CorrectsENcf?.Trim().StartsWith("E46", StringComparison.OrdinalIgnoreCase) == true || NcfNormalizer.Normalize(dto.References?.CorrectsENcf).StartsWith("B16", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Fallback obligatorio DGII para Notas de Crédito/Débito que modifican facturas de exportación (E46)
                        sb.AppendLine("      <IdentificadorExtranjero>EXTRANJERO</IdentificadorExtranjero>");
                    }
                }
                var rawComprador = string.IsNullOrWhiteSpace(dto.Header?.RazonSocialComprador)
                    ? (tipoEcf == "47" ? "Beneficiario del Exterior" : (tipoEcf == "46" ? "Comprador Internacional" : "Consumidor Final"))
                    : dto.Header.RazonSocialComprador;
                var safeComprador = rawComprador.Length > 150 ? rawComprador[..150] : rawComprador;
                var razonSocialComprador = EscapeXml(safeComprador);
                sb.AppendLine($"      <RazonSocialComprador>{razonSocialComprador}</RazonSocialComprador>");
                if (tipoEcf != "47" && !string.IsNullOrWhiteSpace(dto.Header?.CorreoComprador))
                {
                    var trimmedEmail = dto.Header.CorreoComprador.Trim();
                    if (trimmedEmail.Contains(';') || trimmedEmail.Contains(','))
                    {
                        var first = trimmedEmail.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                        if (!string.IsNullOrWhiteSpace(first))
                        {
                            trimmedEmail = first;
                        }
                    }
                    if (trimmedEmail.Length > 80) trimmedEmail = trimmedEmail[..80];
                    if (System.Text.RegularExpressions.Regex.IsMatch(trimmedEmail, @"^\w+([-+.]\w+)*@\w+([-.]\w+)*\.\w+([-.]\w+)*$"))
                    {
                        sb.AppendLine($"      <CorreoComprador>{EscapeXml(trimmedEmail)}</CorreoComprador>");
                    }
                }
                sb.AppendLine("    </Comprador>");
            }

            var (isForeignCurrency, foreignCurrencyCode, exchangeRate, _) = ResolveCanonicalCurrency(dto.Currency);

            var hasOnly16Bucket = dto.Totals?.TaxBuckets is { Count: > 0 } tbCheck
                && tbCheck.All(b => b.Rate == 16)
                && tbCheck.Any(b => b.Base > 0);

            var indicadorFacturacion = tipoEcf switch
            {
                "43" or "44" or "47" => "4", // Exento (DGII Nota 50)
                "46" => "3",                 // ITBIS tasa cero (DGII Nota 51)
                _ => (dto.Totals?.MontoExento > 0 && (dto.Totals.MontoGravadoTotal == null || dto.Totals.MontoGravadoTotal == 0) && (dto.Totals.MontoItbis == 0))
                    ? "4"
                    : (hasOnly16Bucket ? "2" : "1")
            };

            var indicadorBienoServicio = tipoEcf == "47"
                ? "2" // Servicio (DGII Nota 54)
                : (tipoEcf == "41" && dto.Retention?.MontoIsrRetenido > 0 ? "2" : "1");

            List<ProcessedLineItem> processedItems;
            if (dto.Lines != null && dto.Lines.Count > 0)
            {
                processedItems = NormalizeCanonicalLines(dto.Lines);
            }
            else
            {
                var fallbackAmount = Math.Max(0m,
                    tipoEcf is "43" or "44" or "46" or "47"
                        ? (dto.Totals?.MontoTotal ?? 0m)
                        : ((dto.Totals?.MontoSubtotal ?? 0m) > 0m
                            ? dto.Totals!.MontoSubtotal
                            : Math.Max(0m, (dto.Totals?.MontoTotal ?? 0m) - (dto.Totals?.MontoItbis ?? 0m))));

                processedItems = new List<ProcessedLineItem>
                {
                    new ProcessedLineItem
                    {
                        LineNumber = 1,
                        Name = "Item General",
                        Quantity = 1m,
                        UnitPrice = fallbackAmount,
                        DiscountAmount = 0m,
                        MontoItem = fallbackAmount,
                        IndicadorFacturacion = indicadorFacturacion
                    }
                };
            }

            foreach (var item in processedItems)
            {
                item.EffectiveIndicador = tipoEcf switch
                {
                    "46" => item.IndicadorFacturacion == "0" ? "0" : "3",
                    "43" or "44" or "47" => "4",
                    _ => !string.IsNullOrWhiteSpace(item.IndicadorFacturacion) ? item.IndicadorFacturacion! : indicadorFacturacion
                };

                if (isForeignCurrency)
                {
                    item.PrecioOtraMoneda = Math.Round(item.UnitPrice, 4, MidpointRounding.AwayFromZero);
                    item.DiscountOtraMoneda = (tipoEcf is not "43" and not "47" && item.DiscountAmount > 0m)
                        ? Math.Round(item.DiscountAmount, 2, MidpointRounding.AwayFromZero)
                        : 0m;
                    item.MontoItemOtraMoneda = Math.Round(item.MontoItem, 2, MidpointRounding.AwayFromZero);
                    item.DiscountDop = item.DiscountOtraMoneda > 0m
                        ? Math.Round(item.DiscountOtraMoneda * exchangeRate, 2, MidpointRounding.AwayFromZero)
                        : 0m;

                    if (item.Quantity == 1m && item.DiscountDop == 0m)
                    {
                        item.MontoItemDop = Math.Round(item.MontoItemOtraMoneda * exchangeRate, 2, MidpointRounding.AwayFromZero);
                        item.UnitPriceDop = item.MontoItemDop;
                    }
                    else
                    {
                        item.UnitPriceDop = Math.Round(item.PrecioOtraMoneda * exchangeRate, 4, MidpointRounding.AwayFromZero);
                        item.MontoItemDop = Math.Max(0m, Math.Round(item.Quantity * item.UnitPriceDop, 2, MidpointRounding.AwayFromZero) - item.DiscountDop);
                    }
                }
                else
                {
                    item.UnitPriceDop = item.UnitPrice;
                    item.DiscountDop = (tipoEcf is not "43" and not "47") ? item.DiscountAmount : 0m;
                    item.MontoItemDop = item.MontoItem;
                }
            }

            var rawRetention = (tipoEcf is "31" or "33" or "34" or "41" or "47") ? dto.Retention : null;
            CanonicalRetentionDto? effectiveRetention = rawRetention;
            if (isForeignCurrency && rawRetention != null)
            {
                effectiveRetention = new CanonicalRetentionDto
                {
                    IndicadorAgenteRetencionoPercepcion = rawRetention.IndicadorAgenteRetencionoPercepcion,
                    MontoItbisRetenido = rawRetention.MontoItbisRetenido.HasValue
                        ? Math.Round(rawRetention.MontoItbisRetenido.Value * exchangeRate, 2, MidpointRounding.AwayFromZero)
                        : null,
                    MontoIsrRetenido = rawRetention.MontoIsrRetenido.HasValue
                        ? Math.Round(rawRetention.MontoIsrRetenido.Value * exchangeRate, 2, MidpointRounding.AwayFromZero)
                        : null
                };
            }

            computedTotalDop = 0m;
            computedItbisDop = 0m;
            StringBuilder? otraMonedaSb = null;

            sb.AppendLine("    <Totales>");
            if (!isForeignCurrency)
            {
                if (dto.Totals != null)
                {
                    var total = dto.Totals.MontoTotal;
                    var itbis = dto.Totals.MontoItbis;

                    var gravado = dto.Totals.MontoGravadoTotal ?? dto.Totals.MontoSubtotal;
                    var exento = dto.Totals.MontoExento ?? 0m;

                    if (tipoEcf is "43" or "44")
                    {
                        if (total > 0)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoExento>{total:F2}</MontoExento>");
                        }
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotal>{total:F2}</MontoTotal>");
                        computedTotalDop = total;
                    }
                    else if (tipoEcf == "47")
                    {
                        if (total > 0)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoExento>{total:F2}</MontoExento>");
                        }
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotal>{total:F2}</MontoTotal>");
                        if (effectiveRetention?.MontoIsrRetenido is { } isrTotal && isrTotal > 0)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalISRRetencion>{isrTotal:F2}</TotalISRRetencion>");
                        }
                        computedTotalDop = total;
                    }
                    else if (tipoEcf == "46")
                    {
                        if (total > 0)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoTotal>{total:F2}</MontoGravadoTotal>");
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoI3>{total:F2}</MontoGravadoI3>");
                            sb.AppendLine("      <ITBIS3>0</ITBIS3>");
                            sb.AppendLine("      <TotalITBIS>0.00</TotalITBIS>");
                            sb.AppendLine("      <TotalITBIS3>0.00</TotalITBIS3>");
                        }
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotal>{total:F2}</MontoTotal>");
                        computedTotalDop = total;
                    }
                    else
                    {
                        if (gravado > 0)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoTotal>{gravado:F2}</MontoGravadoTotal>");
                        }

                        var slotBase = new decimal?[4];
                        var slotRate = new int?[4];
                        var slotTax = new decimal?[4];

                        if (dto.Totals.TaxBuckets is { Count: > 0 } buckets)
                        {
                            foreach (var bucket in buckets)
                            {
                                var slot = DgiiItbisSlots[bucket.Rate];
                                slotBase[slot] = (slotBase[slot] ?? 0m) + bucket.Base;
                                slotTax[slot] = (slotTax[slot] ?? 0m) + bucket.Tax;
                                slotRate[slot] = bucket.Rate;
                            }
                        }
                        else if (gravado > 0)
                        {
                            slotBase[1] = gravado;
                            slotTax[1] = itbis;
                        }

                        for (var slot = 1; slot <= 3; slot++)
                        {
                            if (slotBase[slot] is { } montoGravado)
                            {
                                sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoI{slot}>{montoGravado:F2}</MontoGravadoI{slot}>");
                            }
                        }
                        if (exento > 0)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoExento>{exento:F2}</MontoExento>");
                        }
                        for (var slot = 1; slot <= 3; slot++)
                        {
                            if (slotRate[slot] is { } rate)
                            {
                                sb.AppendLine($"      <ITBIS{slot}>{rate}</ITBIS{slot}>");
                            }
                        }
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBIS>{itbis:F2}</TotalITBIS>");
                        for (var slot = 1; slot <= 3; slot++)
                        {
                            if (slotTax[slot] is { } montoItbis)
                            {
                                sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBIS{slot}>{montoItbis:F2}</TotalITBIS{slot}>");
                            }
                        }
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotal>{total:F2}</MontoTotal>");
                        if (tipoEcf is "31" or "33" or "34" or "41")
                        {
                            if (tipoEcf == "41" || (effectiveRetention?.MontoItbisRetenido is { } itbisRet && itbisRet > 0))
                            {
                                var itbisRetVal = effectiveRetention?.MontoItbisRetenido ?? 0m;
                                sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBISRetenido>{itbisRetVal:F2}</TotalITBISRetenido>");
                            }
                            if (effectiveRetention?.MontoIsrRetenido is { } isrRet && isrRet > 0)
                            {
                                sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalISRRetencion>{isrRet:F2}</TotalISRRetencion>");
                            }
                        }
                        computedTotalDop = total;
                        computedItbisDop = itbis;
                    }
                }
                else
                {
                    sb.AppendLine("      <MontoTotal>0.00</MontoTotal>");
                }
            }
            else
            {
                // Foreign currency (Otra Moneda): DGII requires <Totales> in DOP (Moneda Nacional)
                // and <OtraMoneda> immediately after </Totales> in the foreign currency.
                otraMonedaSb = new StringBuilder();
                otraMonedaSb.AppendLine("    <OtraMoneda>");
                otraMonedaSb.AppendLine($"      <TipoMoneda>{EscapeXml(foreignCurrencyCode)}</TipoMoneda>");
                otraMonedaSb.AppendLine($"      <TipoCambio>{FormatDecimalUpTo4(exchangeRate)}</TipoCambio>");

                if (tipoEcf is "43" or "44" or "47")
                {
                    var exentoOtra = processedItems.Sum(x => x.MontoItemOtraMoneda);
                    var exentoDop = processedItems.Sum(x => x.MontoItemDop);

                    if (exentoDop > 0m)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoExento>{exentoDop:F2}</MontoExento>");
                    }
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotal>{exentoDop:F2}</MontoTotal>");
                    if (tipoEcf == "47" && effectiveRetention?.MontoIsrRetenido is { } isrTotalDop && isrTotalDop > 0m)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalISRRetencion>{isrTotalDop:F2}</TotalISRRetencion>");
                    }

                    if (exentoOtra > 0m)
                    {
                        otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoExentoOtraMoneda>{exentoOtra:F2}</MontoExentoOtraMoneda>");
                    }
                    otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotalOtraMoneda>{exentoOtra:F2}</MontoTotalOtraMoneda>");
                    computedTotalDop = exentoDop;
                    computedItbisDop = 0m;
                }
                else if (tipoEcf == "46")
                {
                    var gravado3Otra = processedItems.Where(x => x.EffectiveIndicador != "0").Sum(x => x.MontoItemOtraMoneda);
                    var gravado3Dop = processedItems.Where(x => x.EffectiveIndicador != "0").Sum(x => x.MontoItemDop);

                    if (gravado3Dop > 0m)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoTotal>{gravado3Dop:F2}</MontoGravadoTotal>");
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoI3>{gravado3Dop:F2}</MontoGravadoI3>");
                        sb.AppendLine("      <ITBIS3>0</ITBIS3>");
                        sb.AppendLine("      <TotalITBIS>0.00</TotalITBIS>");
                        sb.AppendLine("      <TotalITBIS3>0.00</TotalITBIS3>");
                    }
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotal>{gravado3Dop:F2}</MontoTotal>");

                    if (gravado3Otra > 0m)
                    {
                        otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoTotalOtraMoneda>{gravado3Otra:F2}</MontoGravadoTotalOtraMoneda>");
                        otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravado3OtraMoneda>{gravado3Otra:F2}</MontoGravado3OtraMoneda>");
                        otraMonedaSb.AppendLine("      <TotalITBISOtraMoneda>0.00</TotalITBISOtraMoneda>");
                        otraMonedaSb.AppendLine("      <TotalITBIS3OtraMoneda>0.00</TotalITBIS3OtraMoneda>");
                    }
                    otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotalOtraMoneda>{gravado3Otra:F2}</MontoTotalOtraMoneda>");
                    computedTotalDop = gravado3Dop;
                    computedItbisDop = 0m;
                }
                else
                {
                    var slotBaseOtra = new decimal?[4];
                    var slotTaxOtra = new decimal?[4];
                    var slotBaseDop = new decimal?[4];
                    var slotTaxDop = new decimal?[4];
                    var slotRate = new int?[4];
                    decimal exentoOtra;
                    decimal exentoDop;

                    var linesHaveExplicitIndicators = dto.Lines != null && dto.Lines.Count > 0 &&
                        dto.Lines.Any(l => !string.IsNullOrWhiteSpace(l.IndicadorFacturacion));

                    if (!linesHaveExplicitIndicators && dto.Totals != null && dto.Totals.TaxBuckets is { Count: > 0 } buckets)
                    {
                        foreach (var bucket in buckets)
                        {
                            var slot = DgiiItbisSlots[bucket.Rate];
                            slotBaseOtra[slot] = (slotBaseOtra[slot] ?? 0m) + Math.Round(bucket.Base, 2, MidpointRounding.AwayFromZero);
                            slotTaxOtra[slot] = (slotTaxOtra[slot] ?? 0m) + Math.Round(bucket.Tax, 2, MidpointRounding.AwayFromZero);
                            slotRate[slot] = bucket.Rate;
                        }
                        exentoOtra = Math.Round(dto.Totals.MontoExento ?? 0m, 2, MidpointRounding.AwayFromZero);

                        for (var slot = 1; slot <= 3; slot++)
                        {
                            if (slotBaseOtra[slot] is { } bOtra)
                            {
                                slotBaseDop[slot] = Math.Round(bOtra * exchangeRate, 2, MidpointRounding.AwayFromZero);
                            }
                        }
                        exentoDop = exentoOtra > 0m ? Math.Round(exentoOtra * exchangeRate, 2, MidpointRounding.AwayFromZero) : 0m;

                        var sumLinesDop = processedItems.Where(x => x.EffectiveIndicador != "0").Sum(x => x.MontoItemDop);
                        var currentBaseDop = (slotBaseDop[1] ?? 0m) + (slotBaseDop[2] ?? 0m) + (slotBaseDop[3] ?? 0m) + exentoDop;
                        var diffDop = sumLinesDop - currentBaseDop;
                        if (diffDop != 0m && Math.Abs(diffDop) <= 0.10m)
                        {
                            if (slotBaseDop[1] > 0m) slotBaseDop[1] += diffDop;
                            else if (slotBaseDop[2] > 0m) slotBaseDop[2] += diffDop;
                            else if (slotBaseDop[3] > 0m) slotBaseDop[3] += diffDop;
                            else if (exentoDop > 0m) exentoDop += diffDop;
                        }
                    }
                    else if (!linesHaveExplicitIndicators && dto.Totals != null && (dto.Totals.MontoExento > 0m && (dto.Totals.MontoGravadoTotal ?? 0m) > 0m))
                    {
                        var gOtra = Math.Round(dto.Totals.MontoGravadoTotal ?? dto.Totals.MontoSubtotal, 2, MidpointRounding.AwayFromZero);
                        exentoOtra = Math.Round(dto.Totals.MontoExento ?? 0m, 2, MidpointRounding.AwayFromZero);
                        slotBaseOtra[1] = gOtra;
                        slotTaxOtra[1] = Math.Round(dto.Totals.MontoItbis, 2, MidpointRounding.AwayFromZero);
                        slotRate[1] = 18;

                        slotBaseDop[1] = Math.Round(gOtra * exchangeRate, 2, MidpointRounding.AwayFromZero);
                        exentoDop = Math.Round(exentoOtra * exchangeRate, 2, MidpointRounding.AwayFromZero);

                        var sumLinesDop = processedItems.Where(x => x.EffectiveIndicador != "0").Sum(x => x.MontoItemDop);
                        var diffDop = sumLinesDop - ((slotBaseDop[1] ?? 0m) + exentoDop);
                        if (diffDop != 0m && Math.Abs(diffDop) <= 0.10m)
                        {
                            slotBaseDop[1] += diffDop;
                        }
                    }
                    else
                    {
                        foreach (var item in processedItems)
                        {
                            switch (item.EffectiveIndicador)
                            {
                                case "1":
                                    slotBaseOtra[1] = (slotBaseOtra[1] ?? 0m) + item.MontoItemOtraMoneda;
                                    slotBaseDop[1] = (slotBaseDop[1] ?? 0m) + item.MontoItemDop;
                                    slotRate[1] = 18;
                                    break;
                                case "2":
                                    slotBaseOtra[2] = (slotBaseOtra[2] ?? 0m) + item.MontoItemOtraMoneda;
                                    slotBaseDop[2] = (slotBaseDop[2] ?? 0m) + item.MontoItemDop;
                                    slotRate[2] = 16;
                                    break;
                                case "3":
                                    slotBaseOtra[3] = (slotBaseOtra[3] ?? 0m) + item.MontoItemOtraMoneda;
                                    slotBaseDop[3] = (slotBaseDop[3] ?? 0m) + item.MontoItemDop;
                                    slotRate[3] = 0;
                                    break;
                            }
                        }

                        exentoOtra = processedItems.Where(x => x.EffectiveIndicador == "4").Sum(x => x.MontoItemOtraMoneda);
                        exentoDop = processedItems.Where(x => x.EffectiveIndicador == "4").Sum(x => x.MontoItemDop);

                        if (slotBaseOtra[1] is { } b1) slotTaxOtra[1] = Math.Round(b1 * 0.18m, 2, MidpointRounding.AwayFromZero);
                        if (slotBaseOtra[2] is { } b2) slotTaxOtra[2] = Math.Round(b2 * 0.16m, 2, MidpointRounding.AwayFromZero);
                        if (slotBaseOtra[3].HasValue) slotTaxOtra[3] = 0m;
                    }

                    if (slotBaseDop[1] is { } d1) slotTaxDop[1] = Math.Round(d1 * 0.18m, 2, MidpointRounding.AwayFromZero);
                    if (slotBaseDop[2] is { } d2) slotTaxDop[2] = Math.Round(d2 * 0.16m, 2, MidpointRounding.AwayFromZero);
                    if (slotBaseDop[3].HasValue) slotTaxDop[3] = 0m;

                    var gravadoOtra = (slotBaseOtra[1] ?? 0m) + (slotBaseOtra[2] ?? 0m) + (slotBaseOtra[3] ?? 0m);
                    var itbisOtra = (slotTaxOtra[1] ?? 0m) + (slotTaxOtra[2] ?? 0m) + (slotTaxOtra[3] ?? 0m);
                    var totalOtra = gravadoOtra + exentoOtra + itbisOtra;

                    var gravadoDop = (slotBaseDop[1] ?? 0m) + (slotBaseDop[2] ?? 0m) + (slotBaseDop[3] ?? 0m);
                    var itbisDop = (slotTaxDop[1] ?? 0m) + (slotTaxDop[2] ?? 0m) + (slotTaxDop[3] ?? 0m);
                    var totalDop = gravadoDop + exentoDop + itbisDop;

                    if (tipoEcf == "41" && effectiveRetention != null && rawRetention?.MontoItbisRetenido.HasValue == true &&
                        dto.Totals != null && Math.Abs(rawRetention.MontoItbisRetenido.Value - dto.Totals.MontoItbis) <= 0.02m)
                    {
                        effectiveRetention.MontoItbisRetenido = itbisDop;
                    }

                    if (gravadoDop > 0m)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoTotal>{gravadoDop:F2}</MontoGravadoTotal>");
                    }
                    for (var slot = 1; slot <= 3; slot++)
                    {
                        if (slotBaseDop[slot] is { } montoGravadoDop)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoI{slot}>{montoGravadoDop:F2}</MontoGravadoI{slot}>");
                        }
                    }
                    if (exentoDop > 0m)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoExento>{exentoDop:F2}</MontoExento>");
                    }
                    for (var slot = 1; slot <= 3; slot++)
                    {
                        if (slotRate[slot] is { } rate)
                        {
                            sb.AppendLine($"      <ITBIS{slot}>{rate}</ITBIS{slot}>");
                        }
                    }
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBIS>{itbisDop:F2}</TotalITBIS>");
                    for (var slot = 1; slot <= 3; slot++)
                    {
                        if (slotTaxDop[slot] is { } montoItbisDop)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBIS{slot}>{montoItbisDop:F2}</TotalITBIS{slot}>");
                        }
                    }
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotal>{totalDop:F2}</MontoTotal>");
                    if (tipoEcf is "31" or "33" or "34" or "41")
                    {
                        if (tipoEcf == "41" || (effectiveRetention?.MontoItbisRetenido is { } itbisRet && itbisRet > 0))
                        {
                            var itbisRetVal = effectiveRetention?.MontoItbisRetenido ?? 0m;
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBISRetenido>{itbisRetVal:F2}</TotalITBISRetenido>");
                        }
                        if (effectiveRetention?.MontoIsrRetenido is { } isrRet && isrRet > 0)
                        {
                            sb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalISRRetencion>{isrRet:F2}</TotalISRRetencion>");
                        }
                    }

                    if (gravadoOtra > 0m)
                    {
                        otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravadoTotalOtraMoneda>{gravadoOtra:F2}</MontoGravadoTotalOtraMoneda>");
                    }
                    for (var slot = 1; slot <= 3; slot++)
                    {
                        if (slotBaseOtra[slot] is { } montoGravadoOtra)
                        {
                            otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoGravado{slot}OtraMoneda>{montoGravadoOtra:F2}</MontoGravado{slot}OtraMoneda>");
                        }
                    }
                    if (exentoOtra > 0m)
                    {
                        otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoExentoOtraMoneda>{exentoOtra:F2}</MontoExentoOtraMoneda>");
                    }
                    if (gravadoOtra > 0m || itbisOtra > 0m)
                    {
                        otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBISOtraMoneda>{itbisOtra:F2}</TotalITBISOtraMoneda>");
                    }
                    for (var slot = 1; slot <= 3; slot++)
                    {
                        if (slotTaxOtra[slot] is { } montoItbisOtra)
                        {
                            otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <TotalITBIS{slot}OtraMoneda>{montoItbisOtra:F2}</TotalITBIS{slot}OtraMoneda>");
                        }
                    }
                    otraMonedaSb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoTotalOtraMoneda>{totalOtra:F2}</MontoTotalOtraMoneda>");

                    computedTotalDop = totalDop;
                    computedItbisDop = itbisDop;
                }

                otraMonedaSb.Append("    </OtraMoneda>");
            }
            sb.AppendLine("    </Totales>");
            if (otraMonedaSb != null)
            {
                sb.AppendLine(otraMonedaSb.ToString());
            }
            sb.AppendLine("  </Encabezado>");

            sb.AppendLine("  <DetallesItems>");
            foreach (var item in processedItems)
            {
                sb.AppendLine("    <Item>");
                sb.AppendLine($"      <NumeroLinea>{item.LineNumber}</NumeroLinea>");
                sb.AppendLine($"      <IndicadorFacturacion>{item.EffectiveIndicador}</IndicadorFacturacion>");
                AppendRetencion(sb, effectiveRetention, tipoEcf);
                sb.AppendLine($"      <NombreItem>{EscapeXml(item.Name)}</NombreItem>");
                sb.AppendLine($"      <IndicadorBienoServicio>{indicadorBienoServicio}</IndicadorBienoServicio>");
                if (!string.IsNullOrWhiteSpace(item.Description))
                {
                    sb.AppendLine($"      <DescripcionItem>{EscapeXml(item.Description)}</DescripcionItem>");
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"      <CantidadItem>{item.Quantity:F2}</CantidadItem>");
                var hasSubCentUnitPrice = Math.Round(item.UnitPriceDop, 2, MidpointRounding.AwayFromZero) != Math.Round(item.UnitPriceDop, 4, MidpointRounding.AwayFromZero);
                var precioDopStr = (isForeignCurrency || hasSubCentUnitPrice)
                    ? FormatDecimalUpTo4(item.UnitPriceDop)
                    : item.UnitPriceDop.ToString("F2", CultureInfo.InvariantCulture);
                sb.AppendLine($"      <PrecioUnitarioItem>{precioDopStr}</PrecioUnitarioItem>");
                if (tipoEcf is not "43" and not "47" && item.DiscountDop > 0m)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"      <DescuentoMonto>{item.DiscountDop:F2}</DescuentoMonto>");
                    sb.AppendLine("      <TablaSubDescuento>");
                    sb.AppendLine("        <SubDescuento>");
                    sb.AppendLine("          <TipoSubDescuento>$</TipoSubDescuento>");
                    sb.AppendLine(CultureInfo.InvariantCulture, $"          <MontoSubDescuento>{item.DiscountDop:F2}</MontoSubDescuento>");
                    sb.AppendLine("        </SubDescuento>");
                    sb.AppendLine("      </TablaSubDescuento>");
                }
                if (isForeignCurrency)
                {
                    sb.AppendLine("      <OtraMonedaDetalle>");
                    sb.AppendLine($"        <PrecioOtraMoneda>{FormatDecimalUpTo4(item.PrecioOtraMoneda)}</PrecioOtraMoneda>");
                    if (tipoEcf is not "43" and not "47" && item.DiscountOtraMoneda > 0m)
                    {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"        <DescuentoOtraMoneda>{item.DiscountOtraMoneda:F2}</DescuentoOtraMoneda>");
                    }
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        <MontoItemOtraMoneda>{item.MontoItemOtraMoneda:F2}</MontoItemOtraMoneda>");
                    sb.AppendLine("      </OtraMonedaDetalle>");
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"      <MontoItem>{item.MontoItemDop:F2}</MontoItem>");
                sb.AppendLine("    </Item>");
            }
            sb.AppendLine("  </DetallesItems>");

            // "InformacionReferencia" — top-level sibling AFTER DetallesItems.
            // Obligatorio para 33 y 34; fuera de 33/34 solo válido para contingencia (CodigoModificacion = 4).
            if ((tipoEcf is "33" or "34" || dto.References?.CodigoModificacion == 4) && dto.References is { } refs && !string.IsNullOrWhiteSpace(refs.CorrectsENcf))
            {
                var ncfMod = NcfNormalizer.Normalize(refs.CorrectsENcf);

                sb.AppendLine("  <InformacionReferencia>");
                sb.AppendLine($"    <NCFModificado>{EscapeXml(ncfMod)}</NCFModificado>");
                if (!string.IsNullOrWhiteSpace(refs.RncOtroContribuyente))
                {
                    sb.AppendLine($"    <RNCOtroContribuyente>{EscapeXml(refs.RncOtroContribuyente.Trim())}</RNCOtroContribuyente>");
                }
                // The real XSD marks FechaNCFModificado minOccurs="1" (structurally required).
                var fechaNcfModificado = NormalizeFechaDgii(refs.FechaNcfModificado);
                sb.AppendLine($"    <FechaNCFModificado>{EscapeXml(fechaNcfModificado)}</FechaNCFModificado>");
                if (refs.CodigoModificacion is { } codigo)
                {
                    sb.AppendLine($"    <CodigoModificacion>{codigo}</CodigoModificacion>");
                }
                else if (tipoEcf is "33" or "34")
                {
                    sb.AppendLine("    <CodigoModificacion>1</CodigoModificacion>");
                }
                if ((tipoEcf is "33" or "34") && !string.IsNullOrWhiteSpace(refs.RazonModificacion))
                {
                    var safeRazon = refs.RazonModificacion.Length > 90 ? refs.RazonModificacion[..90] : refs.RazonModificacion;
                    sb.AppendLine($"    <RazonModificacion>{EscapeXml(safeRazon)}</RazonModificacion>");
                }
                sb.AppendLine("  </InformacionReferencia>");
            }

            var fechaHoraFirma = _clock.GetDominicanNow().ToString("dd-MM-yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            sb.AppendLine($"  <FechaHoraFirma>{fechaHoraFirma}</FechaHoraFirma>");
            sb.AppendLine("</ECF>");
            return sb.ToString();
        }

        /// <summary>
        /// DGII's CantidadItem is Decimal18D1or2ValidationTypeMayorCero — strictly greater than zero,
        /// no exceptions. ERPs routinely produce lines with no quantity at all (a discount, freight, a
        /// service charge entered as a lump amount), and passing that zero through invalidates the
        /// ENTIRE document, not just the line.
        ///
        /// Such a line is declared as one unit priced at its own amount. Using the line's original
        /// unit price instead would leave quantity × price ≠ amount, which is internally incoherent
        /// even though the schema wouldn't catch it (PrecioUnitarioItem allows zero).
        /// </summary>
        private static (decimal Cantidad, decimal PrecioUnitario) NormalizeLineQuantity(CanonicalLineDto line) =>
            line.Quantity > 0m
                ? (line.Quantity, line.UnitPrice)
                : (1m, line.Amount);

        /// <summary>
        /// Renders a date in the ONLY format DGII's schemas accept: dd-MM-yyyy (FechaValidationType,
        /// pattern "(3[01]|[12][0-9]|0?[1-9])-(1[012]|0?[1-9])-((19|20)\d{2})" in the real XSDs).
        ///
        /// The canonical DTO carries dates in ISO 8601 (yyyy-MM-dd) — that is what ERPConnector's
        /// ApiSubmitStage sends, and it is the correct thing for a jurisdiction-neutral connector to
        /// send: dd-MM-yyyy is a DGII convention, so translating into it belongs on THIS side of the
        /// HTTP contract. Before this existed the ISO value was written into the XML verbatim, failed
        /// the local XSD gate, and came back as a 400 the connector recorded as a terminal rejection.
        ///
        /// Deliberately NOT a lenient DateTime.TryParse: under InvariantCulture "06-08-2026" parses
        /// month-first as 8 June, so a value already in DGII's format would come back out as a
        /// different calendar day. Only the exact ISO shape is converted; anything already valid (or
        /// unrecognized) passes through untouched, leaving the XSD gate as the backstop it already is.
        /// </summary>
        private static readonly string[] AllowedIncomingDateFormats = new[]
        {
            "yyyy-MM-dd",
            "yyyy/MM/dd",
            "dd-MM-yyyy",
            "dd/MM/yyyy",
            "yyyyMMdd",
            "MM-dd-yyyy",
            "MM/dd/yyyy"
        };

        private string NormalizeFechaDgii(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return _clock.GetDominicanNow().Date.ToString(DgiiDateFormat, CultureInfo.InvariantCulture);
            }

            var trimmed = value.Trim();

            if (DateTime.TryParseExact(trimmed, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return trimmed;
            }

            if (DateTime.TryParseExact(trimmed, AllowedIncomingDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return parsed.ToString(DgiiDateFormat, CultureInfo.InvariantCulture);
            }

            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var generalParsed))
            {
                return generalParsed.ToString(DgiiDateFormat, CultureInfo.InvariantCulture);
            }

            throw new ArgumentException($"La fecha '{value}' tiene un formato inválido para DGII.");
        }

        /// <summary>
        /// Emits a per-Item &lt;Retencion&gt; block (tipo 41 only — see BuildXmlFromCanonical). Real
        /// XSD sequence inside Item: IndicadorAgenteRetencionoPercepcion (required),
        /// MontoITBISRetenido (optional), MontoISRRetenido (optional) — verified against the checked-in
        /// DGII schema, not inferred from the prose spec.
        /// </summary>
        private static void AppendRetencion(StringBuilder sb, CanonicalRetentionDto? retention, string tipoEcf)
        {
            if (retention is null && tipoEcf != "41") return;
            if (tipoEcf != "41" && tipoEcf != "47") return;

            var indAgente = retention?.IndicadorAgenteRetencionoPercepcion ?? 1;
            sb.AppendLine("      <Retencion>");
            sb.AppendLine($"        <IndicadorAgenteRetencionoPercepcion>{indAgente}</IndicadorAgenteRetencionoPercepcion>");
            if (tipoEcf == "47")
            {
                var isr = retention?.MontoIsrRetenido ?? 0m;
                sb.AppendLine(CultureInfo.InvariantCulture, $"        <MontoISRRetenido>{isr:F2}</MontoISRRetenido>");
            }
            else
            {
                var itbisRet = retention?.MontoItbisRetenido ?? 0m;
                sb.AppendLine(CultureInfo.InvariantCulture, $"        <MontoITBISRetenido>{itbisRet:F2}</MontoITBISRetenido>");
                if (retention?.MontoIsrRetenido is { } montoIsr && montoIsr > 0)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"        <MontoISRRetenido>{montoIsr:F2}</MontoISRRetenido>");
                }
            }
            sb.AppendLine("      </Retencion>");
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("&", "&amp;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;")
                        .Replace("\"", "&quot;")
                        .Replace("'", "&apos;")
                        .Replace("©", "&#169;")
                        .Replace("€", "&#8364;")
                        .Replace("®", "&#174;");
        }

        private sealed class ProcessedLineItem
        {
            public int LineNumber { get; set; }
            public string Name { get; set; } = string.Empty;
            public string? Description { get; set; }
            public decimal Quantity { get; set; }
            public decimal UnitPrice { get; set; }
            public decimal DiscountAmount { get; set; }
            public decimal MontoItem { get; set; }
            public string? IndicadorFacturacion { get; set; }
            public string EffectiveIndicador { get; set; } = "1";
            public decimal UnitPriceDop { get; set; }
            public decimal DiscountDop { get; set; }
            public decimal MontoItemDop { get; set; }
            public decimal PrecioOtraMoneda { get; set; }
            public decimal DiscountOtraMoneda { get; set; }
            public decimal MontoItemOtraMoneda { get; set; }
        }

        private static List<ProcessedLineItem> NormalizeCanonicalLines(List<CanonicalLineDto> lines)
        {
            var result = new List<ProcessedLineItem>();

            foreach (var line in lines)
            {
                var (rawQty, rawPrice) = NormalizeLineQuantity(line);
                var rawName = !string.IsNullOrWhiteSpace(line.ItemName) ? line.ItemName : "Item";

                // Si la línea es negativa (descuento en QuickBooks)
                if (line.Amount < 0 || rawPrice < 0)
                {
                    var absDiscount = Math.Abs(line.Amount);
                    if (result.Count > 0)
                    {
                        // Absorber como descuento en el ítem anterior
                        var prev = result[^1];
                        if (absDiscount > prev.MontoItem)
                        {
                            throw new InvalidOperationException($"El descuento ({absDiscount:F2}) no puede ser mayor al monto del ítem anterior ({prev.MontoItem:F2}).");
                        }
                        prev.DiscountAmount += absDiscount;
                        prev.MontoItem -= absDiscount;
                        continue;
                    }
                    else
                    {
                        // MED-107: Una línea de descuento no puede preceder al ítem que descuenta
                        throw new InvalidOperationException("Una línea de descuento no puede preceder al ítem que descuenta.");
                    }
                }

                // Regla DGII: Líneas con valor 0 o precio 0 (e.g. líneas informativas como "Total Bultos", "P-142501", "TOTAL METROS", subtotales de ERP)
                // no se agregan al XML que se envía a la DGII.
                if (line.Amount == 0m || rawPrice == 0m)
                {
                    continue;
                }

                var safeQty = rawQty > 0m ? rawQty : 1m;
                var safePrice = rawPrice;
                var safeAmount = line.Amount;
                var effectiveDiscount = 0m;

                if (line.DiscountAmount > 0m)
                {
                    if (Math.Abs((safeQty * safePrice - line.DiscountAmount) - safeAmount) <= 0.05m)
                    {
                        effectiveDiscount = line.DiscountAmount;
                    }
                    else if (Math.Abs((safeQty * safePrice) - safeAmount) <= 0.05m)
                    {
                        // Pre-folded net line (ERPConnector / Ecf.api folded a negative discount line into Amount & UnitPrice).
                        // Reconstruct gross UnitPrice so DGII's CantidadItem * PrecioUnitarioItem - DescuentoMonto == MontoItem holds.
                        var roundedQty = Math.Round(safeQty, 2, MidpointRounding.AwayFromZero);
                        if (roundedQty > 0m)
                        {
                            var grossAmount = safeAmount + line.DiscountAmount;
                            var grossPrice2 = Math.Round(grossAmount / roundedQty, 2, MidpointRounding.AwayFromZero);
                            if (Math.Abs(Math.Round(roundedQty * grossPrice2, 2, MidpointRounding.AwayFromZero) - line.DiscountAmount - safeAmount) <= 0.01m)
                            {
                                safeQty = roundedQty;
                                safePrice = grossPrice2;
                                effectiveDiscount = line.DiscountAmount;
                            }
                            else
                            {
                                var grossPrice4 = Math.Round(grossAmount / roundedQty, 4, MidpointRounding.AwayFromZero);
                                if (Math.Abs(Math.Round(roundedQty * grossPrice4, 2, MidpointRounding.AwayFromZero) - line.DiscountAmount - safeAmount) <= 0.01m)
                                {
                                    safeQty = roundedQty;
                                    safePrice = grossPrice4;
                                    effectiveDiscount = line.DiscountAmount;
                                }
                            }
                        }
                    }
                }

                var shortName = rawName.Length > 80 ? rawName[..80] : rawName;
                string? extendedDesc = rawName.Length > 80
                    ? (rawName.Length > 1000 ? rawName[..1000] : rawName)
                    : null;

                result.Add(new ProcessedLineItem
                {
                    LineNumber = result.Count + 1,
                    Name = shortName,
                    Description = extendedDesc,
                    Quantity = safeQty,
                    UnitPrice = safePrice,
                    DiscountAmount = effectiveDiscount,
                    MontoItem = safeAmount,
                    IndicadorFacturacion = line.IndicadorFacturacion
                });
            }

            if (result.Count == 0)
            {
                throw new InvalidOperationException("El comprobante no posee líneas facturables con importe mayor a cero.");
            }

            return result;
        }
    }
}
