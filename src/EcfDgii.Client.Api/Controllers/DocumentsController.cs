using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EcfDgii.Client.Application.Documents.Dto;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Infrastructure.Configuration;
using EcfDgii.Client.Infrastructure.Security;
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
            "SigningFailed"
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

        private readonly ApplicationDbContext _db;
        private readonly IEcfSequenceManager _sequenceManager;
        private readonly IEcfClient _ecfClient;
        private readonly IEcfXmlSigner _signer;
        private readonly ILogger<DocumentsController> _logger;
        private readonly IClock _clock;
        private readonly string _emisorRnc;
        private readonly string _emisorRazonSocial;

        public DocumentsController(
            ApplicationDbContext db,
            IEcfSequenceManager sequenceManager,
            IEcfClient ecfClient,
            IEcfXmlSigner signer,
            ILogger<DocumentsController> logger,
            IClock clock,
            IOptions<EcfEmisorOptions> emisorOptions)
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
            if (string.Equals(dto.TipoComprobante, "E34", StringComparison.OrdinalIgnoreCase))
            {
                // Tipo 34 (Nota de Crédito): InformacionReferencia is obligatorio (1). NCFModificado
                // and CodigoModificacion are its two obligatorio sub-fields (lines 1113, 1126).
                if (string.IsNullOrWhiteSpace(dto.References?.CorrectsENcf))
                {
                    return BadRequest(new { error = "References.CorrectsENcf (NCFModificado) is required for TipoComprobante E34." });
                }
                if (dto.References?.CodigoModificacion is null)
                {
                    return BadRequest(new { error = "References.CodigoModificacion is required for TipoComprobante E34." });
                }
            }
            else if (string.Equals(dto.TipoComprobante, "E41", StringComparison.OrdinalIgnoreCase))
            {
                // Tipo 41 (Comprobante de Compras): RNCComprador is obligatorio (1) here (vs.
                // condicional for 31/32/33/34) — it's the informal vendor's identity. Retención is
                // obligatorio (1) only for 41 (and 47) — the buyer withholds ITBIS/ISR on the
                // informal seller's behalf.
                if (string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
                {
                    return BadRequest(new { error = "Header.RncComprador is required for TipoComprobante E41 (the informal vendor's RNC/Cédula)." });
                }
                if (dto.Retention is null)
                {
                    return BadRequest(new { error = "Retention is required for TipoComprobante E41." });
                }
            }

            var tenantId = HttpContext.Items["TenantId"]?.ToString() ?? "default-tenant";
            var editSequence = dto.SourceReference.EditSequence ?? string.Empty;

            // Check if document for this TxnId has already been processed
            var existingDoc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SourceTxnId == dto.SourceReference.TxnId);

            if (existingDoc != null)
            {
                return await HandleExistingDocumentAsync(existingDoc, dto, editSequence);
            }

            // 1. Allocate eNCF Sequence
            var eNcf = await _sequenceManager.GetNextEncfAsync(tenantId, dto.TipoComprobante ?? "E31");

            var doc = new EcfDocument
            {
                TenantId = tenantId,
                SourceTxnId = dto.SourceReference.TxnId,
                ENcf = eNcf
            };
            ApplyCanonicalContent(doc, dto, editSequence);

            _db.EcfDocuments.Add(doc);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsTenantTxnUniqueViolation(ex))
            {
                // Another request for the same (TenantId, SourceTxnId) won the race and committed
                // between our SELECT and this INSERT. Our eNCF is wasted — a gap in the sequence,
                // not a duplicate — but we must not create a second document for this invoice.
                // Detach our losing attempt and defer to the winner.
                _db.Entry(doc).State = EntityState.Detached;

                var winner = await _db.EcfDocuments
                    .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SourceTxnId == dto.SourceReference.TxnId);
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

                return await HandleExistingDocumentAsync(winner, dto, editSequence);
            }

            return await SignAndSendAsync(doc);
        }

        private static bool IsTenantTxnUniqueViolation(DbUpdateException ex) =>
            ex.InnerException is PostgresException { SqlState: "23505" } pg
            && pg.ConstraintName == TenantSourceTxnUniqueConstraint;

        /// <summary>
        /// Decides what to do with a document that already exists for this TxnId, based on how far
        /// the prior attempt got. Shared by the normal lookup path and by the concurrent-insert
        /// recovery path above, so both go through identical never-transmitted/uncertain/terminal logic.
        /// </summary>
        private async Task<IActionResult> HandleExistingDocumentAsync(EcfDocument existingDoc, CanonicalDocumentDto dto, string editSequence)
        {
            if (NeverTransmittedStates.Contains(existingDoc.State))
            {
                // Nothing was ever handed to DGII for this eNCF. Safe to reapply the incoming
                // content — whether or not it changed — and retry under the same eNCF.
                ApplyCanonicalContent(existingDoc, dto, editSequence);
                await _db.SaveChangesAsync();
                return await SignAndSendAsync(existingDoc);
            }

            if (existingDoc.State == "Uncertain")
            {
                // We don't know if the prior attempt reached DGII. Ask before doing anything else.
                return await ReconcileUncertainAsync(existingDoc, dto, editSequence);
            }

            // Terminal / known-transmitted states (Signed, SentToDgii, RejectedByDgii, ...).
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
                securityCode = existingDoc.SecurityCode
            });
        }

        /// <summary>
        /// Applies the canonical DTO's content (header, totals, rebuilt XML) onto a document whose
        /// eNCF is already fixed. Used both for a brand-new document and for retrying a document
        /// that never actually reached DGII, where the incoming content may have been edited since.
        ///
        /// RncEmisor AND RazonSocialEmisor deliberately come from this instance's configured
        /// EcfEmisorOptions, not from dto.Header: both are the identity this API signs and transmits
        /// under, and a wrong or missing value from the caller must never produce a validly-signed
        /// e-CF under someone else's RNC or a fabricated legal name. See EcfEmisorOptions for why
        /// this is instance-level rather than per-request.
        /// </summary>
        private void ApplyCanonicalContent(EcfDocument doc, CanonicalDocumentDto dto, string editSequence)
        {
            doc.EditSequence = editSequence;
            doc.DocumentKind = dto.DocumentKind ?? "Invoice";
            doc.Ncf = dto.Ncf;
            doc.RncEmisor = _emisorRnc;
            doc.RncComprador = dto.Header?.RncComprador;
            doc.TotalAmount = dto.Totals?.MontoTotal ?? 0;
            doc.ItbisAmount = dto.Totals?.MontoItbis ?? 0;
            doc.XmlContent = BuildXmlFromCanonical(dto, doc.ENcf, _emisorRnc, _emisorRazonSocial);
            doc.State = "SequenceAllocated";
        }

        /// <summary>
        /// A prior attempt threw while transmitting to DGII, so it's unknown whether DGII actually
        /// received it. Ask DGII directly instead of guessing: resending blindly risks a duplicate
        /// e-CF under the same eNCF, and silently giving up risks losing one DGII never got.
        /// </summary>
        private async Task<IActionResult> ReconcileUncertainAsync(EcfDocument doc, CanonicalDocumentDto dto, string editSequence)
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

            ConsultaEstadoResponse? status;
            try
            {
                status = await _ecfClient.ConsultarEstadoAsync(doc.RncEmisor, doc.ENcf);
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
                doc.State = "SentToDgii";
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
            ApplyCanonicalContent(doc, dto, editSequence);
            await _db.SaveChangesAsync();
            return await SignAndSendAsync(doc);
        }

        private static bool IsNotFoundByDgii(ConsultaEstadoResponse status) =>
            string.Equals(status.Estado?.Trim(), "No encontrado", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Signs and transmits a document whose eNCF is already allocated and persisted
        /// (either just-allocated, or an existing document being retried). Safe to call
        /// repeatedly for the same document: it never touches sequence allocation.
        /// </summary>
        private async Task<IActionResult> SignAndSendAsync(EcfDocument doc)
        {
            // 3. Digital signing & Real Security Code Calculation
            string signedXml;
            try
            {
                signedXml = _signer.SignXml(doc.XmlContent, doc.RncEmisor);
                var secCode = EcfSecurityUtils.CalcularCodigoSeguridad(signedXml).ToUpperInvariant();

                doc.SignedXmlContent = signedXml;
                doc.SecurityCode = secCode;
                doc.State = "Signed";
            }
            catch (Exception ex)
            {
                doc.State = "SigningFailed";
                doc.SignedXmlContent = doc.XmlContent;
                await _db.SaveChangesAsync();
                return BadRequest(new { error = $"Error al firmar digitalmente el XML de e-CF: {ex.Message}" });
            }

            await _db.SaveChangesAsync();

            try
            {
                var fileName = $"{doc.RncEmisor}{doc.ENcf}.xml";
                var response = await _ecfClient.SendEcfAsync(doc.SignedXmlContent, fileName);
                if (response != null && !string.IsNullOrWhiteSpace(response.TrackId))
                {
                    doc.TrackId = response.TrackId;
                    doc.State = "SentToDgii";
                    doc.SentToDgiiAt = _clock.UtcNow.UtcDateTime;
                }
                else
                {
                    doc.State = "RejectedByDgii";
                }
            }
            catch (Exception)
            {
                doc.State = "Uncertain";
            }

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

        [HttpGet("by-source/{txnId}")]
        public async Task<IActionResult> GetBySourceTxnId(string txnId)
        {
            var tenantId = HttpContext.Items["TenantId"]?.ToString() ?? "default-tenant";
            var doc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SourceTxnId == txnId);

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
                receiptDate = doc.ReceiptDate
            });
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
            
            var fechaVencimiento = DateTime.Today.AddYears(1).ToString("dd-MM-yyyy");
            sb.AppendLine($"      <FechaVencimientoSecuencia>{fechaVencimiento}</FechaVencimientoSecuencia>");
            // TipoIngresos is "No corresponde" (obligatoriedad 0) for tipo 41 (Compras) — DGII spec,
            // "Formato Comprobante Fiscal Electrónico (e-CF) V1.0 (1).md" line 186. Every other type
            // this codebase emits (31/34) requires it.
            if (tipoEcf != "41")
            {
                sb.AppendLine("      <TipoIngresos>1</TipoIngresos>");
            }
            sb.AppendLine("      <TipoPago>1</TipoPago>");
            sb.AppendLine("    </IdDoc>");
            
            sb.AppendLine("    <Emisor>");
            // Deliberately emisorRnc/emisorRazonSocial (this instance's configured identity), never
            // dto.Header?.RncEmisor/RazonSocialEmisor — see ApplyCanonicalContent's doc comment.
            sb.AppendLine($"      <RNCEmisor>{emisorRnc}</RNCEmisor>");
            var razonSocialEmisor = EscapeXml(emisorRazonSocial);
            sb.AppendLine($"      <RazonSocialEmisor>{razonSocialEmisor}</RazonSocialEmisor>");
            sb.AppendLine("      <DireccionEmisor>Distrito Nacional, SD</DireccionEmisor>");
            var fechaEmision = string.IsNullOrWhiteSpace(dto.Header?.FechaEmision) 
                ? DateTime.Today.ToString("dd-MM-yyyy") 
                : dto.Header.FechaEmision;
            sb.AppendLine($"      <FechaEmision>{fechaEmision}</FechaEmision>");
            sb.AppendLine("    </Emisor>");

            if (!string.IsNullOrWhiteSpace(dto.Header?.RncComprador) || !string.IsNullOrWhiteSpace(dto.Header?.RazonSocialComprador))
            {
                sb.AppendLine("    <Comprador>");
                if (!string.IsNullOrWhiteSpace(dto.Header?.RncComprador))
                    sb.AppendLine($"      <RNCComprador>{dto.Header.RncComprador}</RNCComprador>");
                
                var razonSocialComprador = EscapeXml(dto.Header?.RazonSocialComprador ?? "Consumidor Final");
                sb.AppendLine($"      <RazonSocialComprador>{razonSocialComprador}</RazonSocialComprador>");
                sb.AppendLine("    </Comprador>");
            }

            sb.AppendLine("    <Totales>");
            if (dto.Totals != null)
            {
                var subtotal = dto.Totals.MontoSubtotal;
                var total = dto.Totals.MontoTotal;
                var itbis = dto.Totals.MontoItbis;
                
                if (subtotal > 0)
                {
                    sb.AppendLine($"      <MontoGravadoTotal>{subtotal:F2}</MontoGravadoTotal>");
                    sb.AppendLine($"      <MontoGravadoI1>{subtotal:F2}</MontoGravadoI1>");
                }
                sb.AppendLine($"      <TotalITBIS>{itbis:F2}</TotalITBIS>");
                sb.AppendLine($"      <TotalITBIS1>{itbis:F2}</TotalITBIS1>");
                sb.AppendLine($"      <MontoTotal>{total:F2}</MontoTotal>");
            }
            else
            {
                sb.AppendLine("      <MontoTotal>0.00</MontoTotal>");
            }
            sb.AppendLine("    </Totales>");
            sb.AppendLine("  </Encabezado>");

            // "F. Información de Referencia" — obligatorio (1) for tipo 34 (Nota de Crédito). Its own
            // top-level section (sibling to Encabezado/DetallesItems per the spec's section lettering
            // A-F), not nested inside Encabezado. Exact sibling placement/ordering within the full
            // e-CF schema is inferred from the field-level obligatoriedad tables, not from an explicit
            // worked XML example in the spec — confirm against the real DGII XSD (see EcfClientOptions.
            // XsdDirectoryPath/ValidateSchemasLocal, not yet wired to validate outbound documents)
            // before trusting this in Certificación.
            if (tipoEcf == "34" && dto.References is { } refs && !string.IsNullOrWhiteSpace(refs.CorrectsENcf))
            {
                sb.AppendLine("  <InformacionReferencia>");
                sb.AppendLine($"    <NCFModificado>{refs.CorrectsENcf}</NCFModificado>");
                if (!string.IsNullOrWhiteSpace(refs.FechaNcfModificado))
                {
                    sb.AppendLine($"    <FechaNCFModificado>{refs.FechaNcfModificado}</FechaNCFModificado>");
                }
                if (refs.CodigoModificacion is { } codigo)
                {
                    sb.AppendLine($"    <CodigoModificacion>{codigo}</CodigoModificacion>");
                }
                if (!string.IsNullOrWhiteSpace(refs.RazonModificacion))
                {
                    sb.AppendLine($"    <RazonModificacion>{EscapeXml(refs.RazonModificacion)}</RazonModificacion>");
                }
                if (!string.IsNullOrWhiteSpace(refs.RncOtroContribuyente))
                {
                    sb.AppendLine($"    <RNCOtroContribuyente>{refs.RncOtroContribuyente}</RNCOtroContribuyente>");
                }
                sb.AppendLine("  </InformacionReferencia>");
            }

            sb.AppendLine("  <DetallesItems>");
            if (dto.Lines != null && dto.Lines.Count > 0)
            {
                foreach (var line in dto.Lines)
                {
                    sb.AppendLine("    <Item>");
                    sb.AppendLine($"      <NumeroLinea>{line.LineNumber}</NumeroLinea>");
                    sb.AppendLine("      <IndicadorFacturacion>1</IndicadorFacturacion>");
                    var itemName = EscapeXml(line.ItemName ?? "Item");
                    sb.AppendLine($"      <NombreItem>{itemName}</NombreItem>");
                    sb.AppendLine("      <IndicadorBienoServicio>1</IndicadorBienoServicio>");
                    sb.AppendLine($"      <CantidadItem>{line.Quantity:F2}</CantidadItem>");
                    sb.AppendLine($"      <PrecioUnitarioItem>{line.UnitPrice:F2}</PrecioUnitarioItem>");
                    sb.AppendLine($"      <MontoItem>{line.Amount:F2}</MontoItem>");
                    sb.AppendLine("    </Item>");
                }
            }
            else
            {
                sb.AppendLine("    <Item>");
                sb.AppendLine("      <NumeroLinea>1</NumeroLinea>");
                sb.AppendLine("      <IndicadorFacturacion>1</IndicadorFacturacion>");
                sb.AppendLine("      <NombreItem>Item General</NombreItem>");
                sb.AppendLine("      <IndicadorBienoServicio>1</IndicadorBienoServicio>");
                sb.AppendLine("      <CantidadItem>1.00</CantidadItem>");
                var defaultTotal = dto.Totals?.MontoTotal ?? 0;
                sb.AppendLine($"      <PrecioUnitarioItem>{defaultTotal:F2}</PrecioUnitarioItem>");
                sb.AppendLine($"      <MontoItem>{defaultTotal:F2}</MontoItem>");
                sb.AppendLine("    </Item>");
            }
            sb.AppendLine("  </DetallesItems>");

            // "Retención" — obligatorio (1) only for tipo 41 (Compras) and 47. Same placement caveat
            // as InformacionReferencia above: inferred from the obligatoriedad tables, not confirmed
            // against a worked XML example or the real XSD.
            if (tipoEcf == "41" && dto.Retention is { } retention)
            {
                sb.AppendLine("  <Retencion>");
                sb.AppendLine($"    <IndicadorAgenteRetencionoPercepcion>{retention.IndicadorAgenteRetencionoPercepcion}</IndicadorAgenteRetencionoPercepcion>");
                sb.AppendLine($"    <MontoITBISRetenido>{retention.MontoItbisRetenido:F2}</MontoITBISRetenido>");
                if (retention.MontoIsrRetenido is { } montoIsr)
                {
                    sb.AppendLine($"    <MontoISRRetenido>{montoIsr:F2}</MontoISRRetenido>");
                }
                sb.AppendLine("  </Retencion>");
            }

            var fechaHoraFirma = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
            sb.AppendLine($"  <FechaHoraFirma>{fechaHoraFirma}</FechaHoraFirma>");
            sb.AppendLine("</ECF>");
            return sb.ToString();
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Replace("&", "&amp;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;")
                        .Replace("\"", "&quot;")
                        .Replace("'", "&apos;");
        }
    }
}
