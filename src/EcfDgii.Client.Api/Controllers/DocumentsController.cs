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

                // DGII enforces MontoTotal(NC) ≤ MontoTotal(e-CF modificado) — a SEMANTIC rule the XSD
                // cannot express (xs:sequence/xs:restriction only check structure and simple-type
                // constraints, not cross-document business rules) and this codebase does not verify:
                // doing so needs a DB lookup of dto.References.CorrectsENcf's own stored total, not
                // implemented here. A document violating this passes schema validation cleanly and is
                // rejected only when DGII itself receives it. Logged explicitly so this is a known,
                // documented limit — not a surprise discovered in Certificación.
                _logger.LogWarning(
                    "e-CF {ENcf} tipo 34 (Nota de Crédito) para NCFModificado {NcfModificado}: el tope " +
                    "'MontoTotal ≤ MontoTotal del e-CF modificado' NO se verifica localmente antes de " +
                    "enviar a DGII — solo el servidor de DGII lo valida.",
                    dto.SourceReference.TxnId, dto.References.CorrectsENcf);
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

            // Verified against the real DGII XSDs (checked into this repo under "Documentación
            // Técnica (XSD)") — the field-level obligatoriedad tables in the prose spec do NOT
            // capture this: FechaVencimientoSecuencia and IndicadorNotaCredito are MUTUALLY
            // EXCLUSIVE in IdDoc's xs:sequence, not both-optional-fields. Tipo 34's IdDoc has no
            // FechaVencimientoSecuencia element at all; every other type this codebase emits (31/41)
            // has no IndicadorNotaCredito element at all.
            if (tipoEcf == "34")
            {
                // IndicadorNotaCreditoType: 0 = fecha de emisión <= 30 días calendario del e-CF
                // afectado (ITBIS rebate right preserved), 1 = > 30 días (no rebate right). Computing
                // the real value needs the referenced document's emission date, which this method
                // doesn't have access to (no DB lookup here) — defaults to 0 (the common case: a
                // correction issued promptly). Known simplification, not yet resolved.
                sb.AppendLine("      <IndicadorNotaCredito>0</IndicadorNotaCredito>");
            }
            else
            {
                var fechaVencimiento = DateTime.Today.AddYears(1).ToString("dd-MM-yyyy");
                sb.AppendLine($"      <FechaVencimientoSecuencia>{fechaVencimiento}</FechaVencimientoSecuencia>");
            }

            // TipoIngresos doesn't exist as an element at all in tipo 41's IdDoc schema (confirmed by
            // running the real XSD — the prose spec's "obligatoriedad 0" undersold how absolute this
            // is). Present (optional) for 31/34.
            if (tipoEcf != "41")
            {
                // DGII's TipoIngresosValidationType (real XSD) is a zero-padded 2-digit enumeration
                // ("01".."06"), not a bare integer — "1" fails schema validation outright. Pre-existing
                // bug, found by actually running the XSD (not something the field-level obligatoriedad
                // tables alone would have caught).
                sb.AppendLine("      <TipoIngresos>01</TipoIngresos>");
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

            // Retención (tipo 41 only) is PER LINE ITEM inside DetallesItems/Item — NOT a document-
            // level section. Confirmed by running the real XSD: initially built as a header-level
            // block after DetallesItems, which is entirely the wrong structure (the field-level
            // obligatoriedad tables give no hint of this at all). Applied uniformly from the single
            // header-level Retention DTO to every item — this codebase doesn't model per-line
            // withholding, and a single-vendor purchase document plausibly has one retention treatment
            // across its lines; known simplification if that's ever not true.
            var retention = tipoEcf == "41" ? dto.Retention : null;

            sb.AppendLine("  <DetallesItems>");
            if (dto.Lines != null && dto.Lines.Count > 0)
            {
                foreach (var line in dto.Lines)
                {
                    sb.AppendLine("    <Item>");
                    sb.AppendLine($"      <NumeroLinea>{line.LineNumber}</NumeroLinea>");
                    sb.AppendLine("      <IndicadorFacturacion>1</IndicadorFacturacion>");
                    AppendRetencion(sb, retention);
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
                AppendRetencion(sb, retention);
                sb.AppendLine("      <NombreItem>Item General</NombreItem>");
                sb.AppendLine("      <IndicadorBienoServicio>1</IndicadorBienoServicio>");
                sb.AppendLine("      <CantidadItem>1.00</CantidadItem>");
                var defaultTotal = dto.Totals?.MontoTotal ?? 0;
                sb.AppendLine($"      <PrecioUnitarioItem>{defaultTotal:F2}</PrecioUnitarioItem>");
                sb.AppendLine($"      <MontoItem>{defaultTotal:F2}</MontoItem>");
                sb.AppendLine("    </Item>");
            }
            sb.AppendLine("  </DetallesItems>");

            // "InformacionReferencia" — confirmed by the real XSD to be a top-level sibling AFTER
            // DetallesItems (not before it, and not nested inside Encabezado — both wrong in the
            // first version of this code, written from the prose spec's field-level tables alone).
            if (tipoEcf == "34" && dto.References is { } refs && !string.IsNullOrWhiteSpace(refs.CorrectsENcf))
            {
                sb.AppendLine("  <InformacionReferencia>");
                sb.AppendLine($"    <NCFModificado>{refs.CorrectsENcf}</NCFModificado>");
                if (!string.IsNullOrWhiteSpace(refs.RncOtroContribuyente))
                {
                    sb.AppendLine($"    <RNCOtroContribuyente>{refs.RncOtroContribuyente}</RNCOtroContribuyente>");
                }
                // The real XSD marks FechaNCFModificado minOccurs="1" (structurally required) even
                // though the prose spec calls it "condicional a...reemplazo de contingencia" — the two
                // documents disagree, and the XSD is authoritative for what DGII's server will accept.
                // Defaults to today when the caller doesn't have the referenced document's real date;
                // known simplification, not a faithful "fecha del NCF modificado" in that case.
                var fechaNcfModificado = string.IsNullOrWhiteSpace(refs.FechaNcfModificado)
                    ? DateTime.Today.ToString("dd-MM-yyyy")
                    : refs.FechaNcfModificado;
                sb.AppendLine($"    <FechaNCFModificado>{fechaNcfModificado}</FechaNCFModificado>");
                if (refs.CodigoModificacion is { } codigo)
                {
                    sb.AppendLine($"    <CodigoModificacion>{codigo}</CodigoModificacion>");
                }
                if (!string.IsNullOrWhiteSpace(refs.RazonModificacion))
                {
                    sb.AppendLine($"    <RazonModificacion>{EscapeXml(refs.RazonModificacion)}</RazonModificacion>");
                }
                sb.AppendLine("  </InformacionReferencia>");
            }

            var fechaHoraFirma = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
            sb.AppendLine($"  <FechaHoraFirma>{fechaHoraFirma}</FechaHoraFirma>");
            sb.AppendLine("</ECF>");
            return sb.ToString();
        }

        /// <summary>
        /// Emits a per-Item &lt;Retencion&gt; block (tipo 41 only — see BuildXmlFromCanonical). Real
        /// XSD sequence inside Item: IndicadorAgenteRetencionoPercepcion (required),
        /// MontoITBISRetenido (optional), MontoISRRetenido (optional) — verified against the checked-in
        /// DGII schema, not inferred from the prose spec.
        /// </summary>
        private static void AppendRetencion(StringBuilder sb, CanonicalRetentionDto? retention)
        {
            if (retention is null) return;

            sb.AppendLine("      <Retencion>");
            sb.AppendLine($"        <IndicadorAgenteRetencionoPercepcion>{retention.IndicadorAgenteRetencionoPercepcion}</IndicadorAgenteRetencionoPercepcion>");
            sb.AppendLine($"        <MontoITBISRetenido>{retention.MontoItbisRetenido:F2}</MontoITBISRetenido>");
            if (retention.MontoIsrRetenido is { } montoIsr)
            {
                sb.AppendLine($"        <MontoISRRetenido>{montoIsr:F2}</MontoISRRetenido>");
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
                        .Replace("'", "&apos;");
        }
    }
}
