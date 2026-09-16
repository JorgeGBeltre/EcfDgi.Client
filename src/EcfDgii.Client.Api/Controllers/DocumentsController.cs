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
using EcfDgii.Client.Infrastructure.Dgii;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Infrastructure.Serialization;
using EcfDgii.Client.Shared.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EcfDgii.Client.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
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
        private readonly IEcfSchemaValidator _schemaValidator;
        private readonly EcfClientOptions _ecfClientOptions;
        private readonly AmbienteEnum _defaultAmbiente;

        public DocumentsController(
            ApplicationDbContext db,
            IEcfSequenceManager sequenceManager,
            IEcfClient ecfClient,
            IEcfXmlSigner signer,
            ILogger<DocumentsController> logger,
            IClock clock,
            IOptions<EcfEmisorOptions> emisorOptions,
            IEcfSchemaValidator schemaValidator,
            IOptions<EcfClientOptions> ecfClientOptions)
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
            _schemaValidator = schemaValidator;
            _ecfClientOptions = ecfClientOptions.Value;
            _defaultAmbiente = _ecfClientOptions.Environment switch
            {
                EcfEnvironment.Test => AmbienteEnum.PreCertificacion,
                EcfEnvironment.Cert => AmbienteEnum.Certificacion,
                EcfEnvironment.Prod => AmbienteEnum.Produccion,
                _ => AmbienteEnum.Certificacion
            };
        }

        private AmbienteEnum ResolveAmbienteEnum(string? rawEnv)
        {
            if (string.IsNullOrWhiteSpace(rawEnv)) return _defaultAmbiente;
            var lower = rawEnv.ToLowerInvariant();
            if (lower == "test" || lower == "testecf" || lower.Contains("precert")) return AmbienteEnum.PreCertificacion;
            if (lower == "cert" || lower == "certecf" || lower.Contains("certific") || lower.Contains("homolog")) return AmbienteEnum.Certificacion;
            if (lower == "prod" || lower == "ecf" || lower.Contains("producc")) return AmbienteEnum.Produccion;
            if (Enum.TryParse<AmbienteEnum>(rawEnv, true, out var parsed)) return parsed;
            return _defaultAmbiente;
        }

        private IEcfXmlSigner ResolveSigner(string tenantId, string rncEmisor, CanonicalCertificateDto? certDto, bool isDefaultFallback = true)
        {
            if (isDefaultFallback)
            {
                return _signer;
            }

            if (certDto != null && !string.IsNullOrWhiteSpace(certDto.CertificateBase64))
            {
                try
                {
                    var bytes = Convert.FromBase64String(certDto.CertificateBase64);
                    var pwd = certDto.Password ?? string.Empty;
                    var cert = X509CertificateLoader.LoadPkcs12(bytes, pwd, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
                    return new EcfXmlSigner(cert);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo cargar certificado base64 para Tenant {TenantId}. Se utilizará respaldo.", tenantId);
                }
            }

            if (!string.IsNullOrWhiteSpace(certDto?.CertificatePath) && System.IO.File.Exists(certDto.CertificatePath))
            {
                try
                {
                    return new EcfXmlSigner(certDto.CertificatePath, certDto.Password ?? string.Empty);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "No se pudo cargar certificado en ruta {Path}. Se utilizará respaldo.", certDto.CertificatePath);
                }
            }

            var defaultCertDir = "/app/certificates";
            if (Directory.Exists(defaultCertDir))
            {
                var tenantCertFile = Path.Combine(defaultCertDir, $"{tenantId}.pfx");
                if (System.IO.File.Exists(tenantCertFile))
                {
                    try
                    {
                        return new EcfXmlSigner(tenantCertFile, certDto?.Password ?? "EcfTestPassword123!");
                    }
                    catch { }
                }

                var rncCertFile = Path.Combine(defaultCertDir, $"{rncEmisor}.pfx");
                if (System.IO.File.Exists(rncCertFile))
                {
                    try
                    {
                        return new EcfXmlSigner(rncCertFile, certDto?.Password ?? "EcfTestPassword123!");
                    }
                    catch { }
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

        private IEcfClient ResolveEcfClient(string rncEmisor, IEcfXmlSigner signer, AmbienteEnum ambiente, bool isDefaultFallback = true)
        {
            if (isDefaultFallback || (signer == _signer && rncEmisor == _emisorRnc && ambiente == _defaultAmbiente))
            {
                return _ecfClient;
            }

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
        }

        [HttpPost]
        public async Task<IActionResult> SubmitCanonicalDocument([FromBody] CanonicalDocumentDto dto)
        {
            if (dto == null || dto.SourceReference == null || string.IsNullOrWhiteSpace(dto.SourceReference.TxnId))
            {
                return BadRequest(new { error = "SourceReference.TxnId is required." });
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
            else if (string.Equals(dto.TipoComprobante, "E31", StringComparison.OrdinalIgnoreCase) ||
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
                var total = dto.Totals?.MontoTotal ?? 0m;
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

            var tenantId = HttpContext.Items["TenantId"]?.ToString()
                ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var hTenant) ? hTenant.ToString() : null)
                ?? dto.TenantId
                ?? "default-tenant";

            var rawEnv = Request.Headers.TryGetValue("X-Environment", out var hEnv) ? hEnv.ToString() : dto.Environment;
            var ambiente = ResolveAmbienteEnum(rawEnv);

            var isDefaultFallback = tenantId == "default-tenant" && string.IsNullOrWhiteSpace(rawEnv) && !Request.Headers.ContainsKey("X-Tenant-Id") && string.IsNullOrWhiteSpace(dto.TenantId);
            var sequenceScope = isDefaultFallback ? "default-tenant" : $"{tenantId}:{ambiente}";

            var editSequence = dto.SourceReference.EditSequence ?? string.Empty;

            // Check if document for this TxnId has already been processed in this ambiente
            var existingDoc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SourceTxnId == dto.SourceReference.TxnId && (d.Ambiente == null || d.Ambiente == ambiente.ToString()));

            if (existingDoc != null)
            {
                return await HandleExistingDocumentAsync(existingDoc, dto, editSequence, ambiente);
            }

            // 1. Allocate eNCF Sequence
            var eNcf = await _sequenceManager.GetNextEncfAsync(sequenceScope, dto.TipoComprobante ?? "E31");

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
                // Another request for the same (TenantId, SourceTxnId, Ambiente) won the race and committed
                // between our SELECT and this INSERT. Our eNCF is wasted — a gap in the sequence,
                // not a duplicate — but we must not create a second document for this invoice.
                // Detach our losing attempt and defer to the winner.
                _db.Entry(doc).State = EntityState.Detached;

                var winner = await _db.EcfDocuments
                    .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SourceTxnId == dto.SourceReference.TxnId && (d.Ambiente == null || d.Ambiente == ambiente.ToString()));
                if (winner == null)
                {
                    // Genuinely unexpected: the constraint fired, so a conflicting row must exist,
                    // but it isn't visible to this re-query. Don't let this surface as a bare NRE
                    // three layers down — log the real cause and fail with a clear, specific error.
                    _logger.LogError(ex,
                        "Unique constraint {Constraint} violated for TenantId={TenantId} SourceTxnId={TxnId}, " +
                        "but no conflicting document was found on re-fetch.",
                        TenantSourceTxnUniqueConstraint, tenantId, dto.SourceReference.TxnId);
                    throw new InvalidOperationException(
                        $"Unique constraint {TenantSourceTxnUniqueConstraint} was violated for TxnId " +
                        $"'{dto.SourceReference.TxnId}', but no conflicting document could be found. " +
                        "This should be impossible; investigate before retrying.", ex);
                }

                return await HandleExistingDocumentAsync(winner, dto, editSequence, ambiente);
            }

            return await SignAndSendAsync(doc, dto, ambiente);
        }

        private static bool IsTenantTxnUniqueViolation(DbUpdateException ex) =>
            ex.InnerException is PostgresException { SqlState: "23505" } pg
            && pg.ConstraintName == TenantSourceTxnUniqueConstraint;

        /// <summary>
        /// Decides what to do with a document that already exists for this TxnId, based on how far
        /// the prior attempt got. Shared by the normal lookup path and by the concurrent-insert
        /// recovery path above, so both go through identical never-transmitted/uncertain/terminal logic.
        /// </summary>
        private async Task<IActionResult> HandleExistingDocumentAsync(EcfDocument existingDoc, CanonicalDocumentDto dto, string editSequence, AmbienteEnum? ambiente = null)
        {
            var isDefaultFallback = existingDoc.TenantId == "default-tenant" && string.IsNullOrWhiteSpace(dto.Environment) && string.IsNullOrWhiteSpace(dto.TenantId);
            if (NeverTransmittedStates.Contains(existingDoc.State))
            {
                // Nothing was ever handed to DGII for this eNCF. Safe to reapply the incoming
                // content — whether or not it changed — and retry under the same eNCF.
                ApplyCanonicalContent(existingDoc, dto, editSequence, isDefaultFallback);
                await _db.SaveChangesAsync();
                return await SignAndSendAsync(existingDoc, dto, ambiente);
            }

            if (existingDoc.State == "Uncertain")
            {
                // We don't know if the prior attempt reached DGII. Ask before doing anything else.
                return await ReconcileUncertainAsync(existingDoc, dto, editSequence, ambiente);
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

            doc.RncEmisor = effectiveRnc;
            doc.RncComprador = dto.Header?.RncComprador;
            doc.TotalAmount = dto.Totals?.MontoTotal ?? 0;
            doc.ItbisAmount = dto.Totals?.MontoItbis ?? 0;
            doc.XmlContent = BuildXmlFromCanonical(dto, doc.ENcf, effectiveRnc, effectiveRazonSocial);
            doc.State = "SequenceAllocated";
        }

        /// <summary>
        /// A prior attempt threw while transmitting to DGII, so it's unknown whether DGII actually
        /// received it. Ask DGII directly instead of guessing: resending blindly risks a duplicate
        /// e-CF under the same eNCF, and silently giving up risks losing one DGII never got.
        /// </summary>
        private async Task<IActionResult> ReconcileUncertainAsync(EcfDocument doc, CanonicalDocumentDto dto, string editSequence, AmbienteEnum? ambiente = null)
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
            var isDefaultFallback = doc.TenantId == "default-tenant" && string.IsNullOrWhiteSpace(dto.Environment) && string.IsNullOrWhiteSpace(dto.TenantId);
            var effectiveSigner = ResolveSigner(doc.TenantId, doc.RncEmisor, dto.Certificate, isDefaultFallback);
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
            return await SignAndSendAsync(doc, dto, ambiente);
        }

        private static bool IsNotFoundByDgii(ConsultaEstadoResponse status) =>
            string.Equals(status.Estado?.Trim(), "No encontrado", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Signs and transmits a document whose eNCF is already allocated and persisted
        /// (either just-allocated, or an existing document being retried). Safe to call
        /// repeatedly for the same document: it never touches sequence allocation.
        /// </summary>
        private async Task<IActionResult> SignAndSendAsync(EcfDocument doc, CanonicalDocumentDto? dto = null, AmbienteEnum? ambiente = null)
        {
            var effectiveAmbiente = ambiente ?? _defaultAmbiente;
            var isDefaultFallback = doc.TenantId == "default-tenant" && string.IsNullOrWhiteSpace(dto?.Environment) && string.IsNullOrWhiteSpace(dto?.TenantId);
            var effectiveSigner = ResolveSigner(doc.TenantId, doc.RncEmisor, dto?.Certificate, isDefaultFallback);
            var effectiveClient = ResolveEcfClient(doc.RncEmisor, effectiveSigner, effectiveAmbiente, isDefaultFallback);

            // 3. Digital signing & Real Security Code Calculation
            string signedXml;
            try
            {
                signedXml = effectiveSigner.SignXml(doc.XmlContent, doc.RncEmisor);
                var secCode = EcfSecurityUtils.CalcularCodigoSeguridad(signedXml).ToUpperInvariant();

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
                var fileName = $"{doc.RncEmisor}{doc.ENcf}.xml";
                var response = await effectiveClient.SendEcfAsync(doc.SignedXmlContent, fileName);
                if (response != null && !string.IsNullOrWhiteSpace(response.TrackId))
                {
                    doc.TrackId = response.TrackId;
                    // DGII received it and issued a TrackId — the signature is confirmed real. This is
                    // the counterpart to "Unsigned": those two states are the signature-validity axis,
                    // and only DGII's acceptance moves a document across it.
                    doc.State = "Signed";
                    doc.SentToDgiiAt = _clock.UtcNow.UtcDateTime;
                }
                else
                {
                    doc.State = "RejectedByDgii";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fallo transmitiendo e-CF {ENcf} (RNC {Rnc}) a DGII: {Message}", doc.ENcf, doc.RncEmisor, ex.Message);
                doc.State = "Uncertain";
            }

            await _db.SaveChangesAsync();

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

        [HttpGet("by-source/{txnId}")]
        public async Task<IActionResult> GetBySourceTxnId(string txnId)
        {
            var tenantId = HttpContext.Items["TenantId"]?.ToString()
                ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var h) ? h.ToString() : null)
                ?? "default-tenant";

            var doc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => (tenantId == "default-tenant" || d.TenantId == tenantId) && (d.SourceTxnId == txnId || d.TrackId == txnId || d.ENcf == txnId));

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
                securityCode = doc.SecurityCode,
                receiptDate = doc.ReceiptDate,
                signedXml = doc.SignedXmlContent
            });
        }

        [HttpGet("by-source/{txnId}/xml")]
        public async Task<IActionResult> GetXmlBySourceTxnId(string txnId)
        {
            var tenantId = HttpContext.Items["TenantId"]?.ToString()
                ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var h) ? h.ToString() : null)
                ?? "default-tenant";

            var doc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => (tenantId == "default-tenant" || d.TenantId == tenantId) && (d.SourceTxnId == txnId || d.TrackId == txnId || d.ENcf == txnId));

            if (doc == null || string.IsNullOrWhiteSpace(doc.SignedXmlContent))
            {
                return NotFound(new { error = $"Document with source TxnId '{txnId}' not found or has no XML." });
            }

            var fileName = $"{doc.RncEmisor}-{doc.ENcf}.xml";
            return File(Encoding.UTF8.GetBytes(doc.SignedXmlContent), "application/xml", fileName);
        }

        [HttpGet("{id:guid}/xml")]
        public async Task<IActionResult> GetXmlById(Guid id)
        {
            var tenantId = HttpContext.Items["TenantId"]?.ToString()
                ?? (Request.Headers.TryGetValue("X-Tenant-Id", out var h) ? h.ToString() : null)
                ?? "default-tenant";

            var doc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => (tenantId == "default-tenant" || d.TenantId == tenantId) && d.Id == id);

            if (doc == null || string.IsNullOrWhiteSpace(doc.SignedXmlContent))
            {
                return NotFound(new { error = $"Document '{id}' not found or has no XML." });
            }

            var fileName = $"{doc.RncEmisor}-{doc.ENcf}.xml";
            return File(Encoding.UTF8.GetBytes(doc.SignedXmlContent), "application/xml", fileName);
        }

        private static string BuildXmlFromCanonical(CanonicalDocumentDto dto, string eNcf, string emisorRnc, string emisorRazonSocial)
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
            
            sb.AppendLine($"      <TipoeCF>{tipoEcf}</TipoeCF>");
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
                // IndicadorNotaCreditoType: 0 = fecha de emisión <= 30 días calendario del e-CF
                // afectado (ITBIS rebate right preserved), 1 = > 30 días (no rebate right). Computing
                // the real value needs the referenced document's emission date, which this method
                // doesn't have access to (no DB lookup here) — defaults to 0 (the common case: a
                // correction issued promptly). Known simplification, not yet resolved.
                sb.AppendLine("      <IndicadorNotaCredito>0</IndicadorNotaCredito>");
            }
            else if (tipoEcf != "32")
            {
                var fechaVencimiento = DateTime.Today.AddYears(1).ToString("dd-MM-yyyy");
                sb.AppendLine($"      <FechaVencimientoSecuencia>{fechaVencimiento}</FechaVencimientoSecuencia>");
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
            sb.AppendLine($"      <RNCEmisor>{emisorRnc}</RNCEmisor>");
            var safeEmisorName = emisorRazonSocial.Length > 150 ? emisorRazonSocial[..150] : emisorRazonSocial;
            var razonSocialEmisor = EscapeXml(safeEmisorName);
            sb.AppendLine($"      <RazonSocialEmisor>{razonSocialEmisor}</RazonSocialEmisor>");
            sb.AppendLine("      <DireccionEmisor>Distrito Nacional, SD</DireccionEmisor>");
            var fechaEmision = NormalizeFechaDgii(dto.Header?.FechaEmision);
            sb.AppendLine($"      <FechaEmision>{fechaEmision}</FechaEmision>");
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
                    if (!string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
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
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
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

            sb.AppendLine("    <Totales>");
            if (dto.Totals != null)
            {
                var total = dto.Totals.MontoTotal;
                var itbis = dto.Totals.MontoItbis;

                // Exempt amounts are their own DGII bucket, not part of the taxed base. Falling back
                // to MontoSubtotal (taxed + exempt lumped together) preserves the pre-split behaviour
                // for a caller that hasn't been updated yet — see CanonicalTotalsDto.
                var gravado = dto.Totals.MontoGravadoTotal ?? dto.Totals.MontoSubtotal;
                var exento = dto.Totals.MontoExento ?? 0m;

                if (tipoEcf is "43" or "44")
                {
                    // Tipos 43 (Gastos Menores) y 44 (Regímenes Especiales): exentos de ITBIS.
                    // El esquema solo admite MontoExento y MontoTotal (prohíbe MontoGravadoTotal y TotalITBIS).
                    if (total > 0)
                    {
                        sb.AppendLine($"      <MontoExento>{total:F2}</MontoExento>");
                    }
                    sb.AppendLine($"      <MontoTotal>{total:F2}</MontoTotal>");
                }
                else if (tipoEcf == "47")
                {
                    // Tipo 47 (Pagos al Exterior): exento de ITBIS local, retención de ISR.
                    if (total > 0)
                    {
                        sb.AppendLine($"      <MontoExento>{total:F2}</MontoExento>");
                    }
                    sb.AppendLine($"      <MontoTotal>{total:F2}</MontoTotal>");
                    if (dto.Retention?.MontoIsrRetenido is { } isrTotal && isrTotal > 0)
                    {
                        sb.AppendLine($"      <TotalISRRetencion>{isrTotal:F2}</TotalISRRetencion>");
                    }
                }
                else if (tipoEcf == "46")
                {
                    // Tipo 46 (Exportaciones): gravadas al 0% ITBIS (bucket 3).
                    // El esquema XSD 46 solo admite MontoGravadoTotal, MontoGravadoI3, ITBIS3, TotalITBIS, TotalITBIS3, MontoTotal.
                    if (total > 0)
                    {
                        sb.AppendLine($"      <MontoGravadoTotal>{total:F2}</MontoGravadoTotal>");
                        sb.AppendLine($"      <MontoGravadoI3>{total:F2}</MontoGravadoI3>");
                        sb.AppendLine("      <ITBIS3>0</ITBIS3>");
                        sb.AppendLine("      <TotalITBIS>0.00</TotalITBIS>");
                        sb.AppendLine("      <TotalITBIS3>0.00</TotalITBIS3>");
                    }
                    sb.AppendLine($"      <MontoTotal>{total:F2}</MontoTotal>");
                }
                else
                {
                    // Element ORDER here is the XSD's Totales xs:sequence, which is not negotiable:
                    // MontoGravadoTotal, I1, I2, I3, MontoExento, ITBIS1-3, TotalITBIS, TotalITBIS1-3,
                    // ..., MontoTotal. Everything except MontoTotal is minOccurs="0", so omitting the
                    // buckets this codebase doesn't populate is valid — misplacing one is not.
                    if (gravado > 0)
                    {
                        sb.AppendLine($"      <MontoGravadoTotal>{gravado:F2}</MontoGravadoTotal>");
                    }

                    // Slot 1..3 = DGII's 18% / 16% / 0% ITBIS buckets. A caller that sends no buckets
                    // predates them: everything taxed goes to I1 and no ITBIS rate elements are emitted,
                    // exactly as before — a version-skewed pair must not produce a different document.
                    var slotBase = new decimal?[4];
                    var slotRate = new int?[4];
                    var slotTax = new decimal?[4];

                    if (dto.Totals.TaxBuckets is { Count: > 0 } buckets)
                    {
                        foreach (var bucket in buckets)
                        {
                            // Rates were validated against the slot map before any sequence was allocated
                            // (see SubmitCanonicalDocument), so every one of them maps here.
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
                            sb.AppendLine($"      <MontoGravadoI{slot}>{montoGravado:F2}</MontoGravadoI{slot}>");
                        }
                    }
                    if (exento > 0)
                    {
                        sb.AppendLine($"      <MontoExento>{exento:F2}</MontoExento>");
                    }
                    // ITBIS1/2/3 declare the RATE of each bucket (Integer2ValidationType — a 1-2 digit
                    // integer, not an amount); TotalITBIS1/2/3 further down carry the amounts.
                    for (var slot = 1; slot <= 3; slot++)
                    {
                        if (slotRate[slot] is { } rate)
                        {
                            sb.AppendLine($"      <ITBIS{slot}>{rate}</ITBIS{slot}>");
                        }
                    }
                    sb.AppendLine($"      <TotalITBIS>{itbis:F2}</TotalITBIS>");
                    for (var slot = 1; slot <= 3; slot++)
                    {
                        if (slotTax[slot] is { } montoItbis)
                        {
                            sb.AppendLine($"      <TotalITBIS{slot}>{montoItbis:F2}</TotalITBIS{slot}>");
                        }
                    }
                    sb.AppendLine($"      <MontoTotal>{total:F2}</MontoTotal>");
                    if (tipoEcf is "31" or "33" or "34" or "41")
                    {
                        if (dto.Retention?.MontoItbisRetenido is { } itbisRet && itbisRet > 0)
                        {
                            sb.AppendLine($"      <TotalITBISRetenido>{itbisRet:F2}</TotalITBISRetenido>");
                        }
                        if (dto.Retention?.MontoIsrRetenido is { } isrRet && isrRet > 0)
                        {
                            sb.AppendLine($"      <TotalISRRetencion>{isrRet:F2}</TotalISRRetencion>");
                        }
                    }
                }
            }
            else
            {
                sb.AppendLine("      <MontoTotal>0.00</MontoTotal>");
            }
            sb.AppendLine("    </Totales>");
            sb.AppendLine("  </Encabezado>");

            // Retención is PER LINE ITEM inside DetallesItems/Item.
            // In tipo 41, ITBIS & ISR retention are supported; 31, 33, 34 also support it per schema.
            // In tipo 47, only ISR retention is supported.
            var retention = (tipoEcf is "31" or "33" or "34" or "41" or "47") ? dto.Retention : null;

            var indicadorFacturacion = tipoEcf switch
            {
                "43" or "44" or "47" => "4", // Exento (DGII Nota 50)
                "46" => "3",                 // ITBIS tasa cero (DGII Nota 51)
                _ => (dto.Totals?.MontoExento > 0 && (dto.Totals.MontoGravadoTotal == null || dto.Totals.MontoGravadoTotal == 0) && (dto.Totals.MontoItbis == 0)) ? "4" : "1"
            };

            var indicadorBienoServicio = tipoEcf == "47"
                ? "2" // Servicio (DGII Nota 54)
                : (tipoEcf == "41" && dto.Retention?.MontoIsrRetenido > 0 ? "2" : "1");

            sb.AppendLine("  <DetallesItems>");
            if (dto.Lines != null && dto.Lines.Count > 0)
            {
                var processedItems = NormalizeCanonicalLines(dto.Lines);
                foreach (var item in processedItems)
                {
                    sb.AppendLine("    <Item>");
                    sb.AppendLine($"      <NumeroLinea>{item.LineNumber}</NumeroLinea>");
                    sb.AppendLine($"      <IndicadorFacturacion>{indicadorFacturacion}</IndicadorFacturacion>");
                    AppendRetencion(sb, retention, tipoEcf);
                    sb.AppendLine($"      <NombreItem>{EscapeXml(item.Name)}</NombreItem>");
                    sb.AppendLine($"      <IndicadorBienoServicio>{indicadorBienoServicio}</IndicadorBienoServicio>");
                    if (!string.IsNullOrWhiteSpace(item.Description))
                    {
                        sb.AppendLine($"      <DescripcionItem>{EscapeXml(item.Description)}</DescripcionItem>");
                    }
                    sb.AppendLine($"      <CantidadItem>{item.Quantity:F2}</CantidadItem>");
                    sb.AppendLine($"      <PrecioUnitarioItem>{item.UnitPrice:F2}</PrecioUnitarioItem>");
                    if (tipoEcf is not "43" and not "47" && item.DiscountAmount > 0)
                    {
                        sb.AppendLine($"      <DescuentoMonto>{item.DiscountAmount:F2}</DescuentoMonto>");
                    }
                    sb.AppendLine($"      <MontoItem>{item.MontoItem:F2}</MontoItem>");
                    sb.AppendLine("    </Item>");
                }
            }
            else
            {
                sb.AppendLine("    <Item>");
                sb.AppendLine("      <NumeroLinea>1</NumeroLinea>");
                sb.AppendLine($"      <IndicadorFacturacion>{indicadorFacturacion}</IndicadorFacturacion>");
                AppendRetencion(sb, retention, tipoEcf);
                sb.AppendLine("      <NombreItem>Item General</NombreItem>");
                sb.AppendLine($"      <IndicadorBienoServicio>{indicadorBienoServicio}</IndicadorBienoServicio>");
                sb.AppendLine("      <CantidadItem>1.00</CantidadItem>");
                var defaultTotal = Math.Max(0m, dto.Totals?.MontoTotal ?? 0);
                sb.AppendLine($"      <PrecioUnitarioItem>{defaultTotal:F2}</PrecioUnitarioItem>");
                sb.AppendLine($"      <MontoItem>{defaultTotal:F2}</MontoItem>");
                sb.AppendLine("    </Item>");
            }
            sb.AppendLine("  </DetallesItems>");

            // "InformacionReferencia" — top-level sibling AFTER DetallesItems.
            // Obligatorio para 33 y 34; opcional para los demás si dto.References != null.
            if ((tipoEcf is "33" or "34" || dto.References != null) && dto.References is { } refs && !string.IsNullOrWhiteSpace(refs.CorrectsENcf))
            {
                var ncfMod = refs.CorrectsENcf.Trim();
                if (ncfMod.Length < 11)
                {
                    if ((ncfMod.StartsWith("1") || ncfMod.StartsWith("2") || ncfMod.StartsWith("4")) && ncfMod.Length == 9)
                    {
                        ncfMod = "B0" + ncfMod;
                    }
                    else if (ncfMod.StartsWith("0") && ncfMod.Length == 10)
                    {
                        ncfMod = "B" + ncfMod;
                    }
                    else if (ncfMod.Length == 8 && ncfMod.All(char.IsDigit))
                    {
                        ncfMod = "B02" + ncfMod;
                    }
                }

                sb.AppendLine("  <InformacionReferencia>");
                sb.AppendLine($"    <NCFModificado>{ncfMod}</NCFModificado>");
                if (!string.IsNullOrWhiteSpace(refs.RncOtroContribuyente))
                {
                    sb.AppendLine($"    <RNCOtroContribuyente>{EscapeXml(refs.RncOtroContribuyente.Trim())}</RNCOtroContribuyente>");
                }
                // The real XSD marks FechaNCFModificado minOccurs="1" (structurally required).
                var fechaNcfModificado = NormalizeFechaDgii(refs.FechaNcfModificado);
                sb.AppendLine($"    <FechaNCFModificado>{fechaNcfModificado}</FechaNCFModificado>");
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

            var fechaHoraFirma = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
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

        private static string NormalizeFechaDgii(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return DateTime.Today.ToString(DgiiDateFormat, CultureInfo.InvariantCulture);
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

            return trimmed.Replace('/', '-');
        }

        /// <summary>
        /// Emits a per-Item &lt;Retencion&gt; block (tipo 41 only — see BuildXmlFromCanonical). Real
        /// XSD sequence inside Item: IndicadorAgenteRetencionoPercepcion (required),
        /// MontoITBISRetenido (optional), MontoISRRetenido (optional) — verified against the checked-in
        /// DGII schema, not inferred from the prose spec.
        /// </summary>
        private static void AppendRetencion(StringBuilder sb, CanonicalRetentionDto? retention, string tipoEcf)
        {
            if (retention is null) return;

            sb.AppendLine("      <Retencion>");
            sb.AppendLine($"        <IndicadorAgenteRetencionoPercepcion>{retention.IndicadorAgenteRetencionoPercepcion}</IndicadorAgenteRetencionoPercepcion>");
            if (tipoEcf == "47")
            {
                var isr = retention.MontoIsrRetenido ?? 0m;
                sb.AppendLine($"        <MontoISRRetenido>{isr:F2}</MontoISRRetenido>");
            }
            else
            {
                sb.AppendLine($"        <MontoITBISRetenido>{retention.MontoItbisRetenido:F2}</MontoITBISRetenido>");
                if (retention.MontoIsrRetenido is { } montoIsr)
                {
                    sb.AppendLine($"        <MontoISRRetenido>{montoIsr:F2}</MontoISRRetenido>");
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
                        prev.DiscountAmount += absDiscount;
                        prev.MontoItem = Math.Max(0m, prev.MontoItem - absDiscount);
                        continue;
                    }
                }

                // Regla DGII: Líneas con valor 0 o precio 0 (e.g. líneas informativas como "Total Bultos", "P-142501", "TOTAL METROS", subtotales de ERP)
                // no se agregan al XML que se envía a la DGII.
                if (line.Amount == 0m || rawPrice == 0m)
                {
                    continue;
                }

                var safeQty = rawQty > 0m ? rawQty : 1m;
                var safePrice = Math.Max(0m, rawPrice);
                var safeAmount = Math.Max(0m, line.Amount);

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
                    DiscountAmount = 0m,
                    MontoItem = safeAmount
                });
            }

            if (result.Count == 0)
            {
                result.Add(new ProcessedLineItem
                {
                    LineNumber = 1,
                    Name = "Item General",
                    Quantity = 1.00m,
                    UnitPrice = 0m,
                    DiscountAmount = 0m,
                    MontoItem = 0m
                });
            }

            return result;
        }
    }
}
