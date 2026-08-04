using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EcfDgii.Client.Application.Documents.Dto;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Persistence;
using EcfDgii.Client.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EcfDgii.Client.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DocumentsController : ControllerBase
    {
        private readonly ApplicationDbContext _db;
        private readonly IEcfSequenceManager _sequenceManager;
        private readonly IEcfClient _ecfClient;
        private readonly IEcfXmlSigner _signer;

        public DocumentsController(
            ApplicationDbContext db,
            IEcfSequenceManager sequenceManager,
            IEcfClient ecfClient,
            IEcfXmlSigner signer)
        {
            _db = db;
            _sequenceManager = sequenceManager;
            _ecfClient = ecfClient;
            _signer = signer;
        }

        [HttpPost]
        public async Task<IActionResult> SubmitCanonicalDocument([FromBody] CanonicalDocumentDto dto)
        {
            if (dto == null || dto.SourceReference == null || string.IsNullOrWhiteSpace(dto.SourceReference.TxnId))
            {
                return BadRequest(new { error = "SourceReference.TxnId is required." });
            }

            var tenantId = HttpContext.Items["TenantId"]?.ToString() ?? "default-tenant";

            // Check if document for this TxnId has already been processed
            var existingDoc = await _db.EcfDocuments
                .FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SourceTxnId == dto.SourceReference.TxnId);

            if (existingDoc != null)
            {
                return Accepted(new
                {
                    documentId = existingDoc.Id,
                    eNcf = existingDoc.ENcf,
                    state = existingDoc.State,
                    trackId = existingDoc.TrackId,
                    securityCode = existingDoc.SecurityCode
                });
            }

            // 1. Allocate eNCF Sequence
            var eNcf = await _sequenceManager.GetNextEncfAsync(tenantId, dto.TipoComprobante ?? "E31");

            // 2. Build Fiscal XML Content
            var rawXml = BuildXmlFromCanonical(dto, eNcf);

            var doc = new EcfDocument
            {
                TenantId = tenantId,
                SourceTxnId = dto.SourceReference.TxnId,
                DocumentKind = dto.DocumentKind ?? "Invoice",
                Ncf = dto.Ncf,
                ENcf = eNcf,
                RncEmisor = dto.Header?.RncEmisor ?? "101010101",
                RncComprador = dto.Header?.RncComprador,
                TotalAmount = dto.Totals?.MontoTotal ?? 0,
                ItbisAmount = dto.Totals?.MontoItbis ?? 0,
                XmlContent = rawXml,
                State = "SequenceAllocated"
            };

            _db.EcfDocuments.Add(doc);
            await _db.SaveChangesAsync();

            // 3. Digital signing & Real Security Code Calculation
            string signedXml;
            string secCode = "000000";
            try
            {
                signedXml = _signer.SignXml(rawXml, doc.RncEmisor);
                secCode = EcfSecurityUtils.CalcularCodigoSeguridad(signedXml).ToUpperInvariant();
                
                doc.SignedXmlContent = signedXml;
                doc.SecurityCode = secCode;
                doc.State = "Signed";
            }
            catch (Exception ex)
            {
                doc.State = "SigningFailed";
                doc.SignedXmlContent = rawXml;
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

        private static string BuildXmlFromCanonical(CanonicalDocumentDto dto, string eNcf)
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
            sb.AppendLine("      <TipoIngresos>1</TipoIngresos>");
            sb.AppendLine("      <TipoPago>1</TipoPago>");
            sb.AppendLine("    </IdDoc>");
            
            sb.AppendLine("    <Emisor>");
            sb.AppendLine($"      <RNCEmisor>{dto.Header?.RncEmisor}</RNCEmisor>");
            var razonSocialEmisor = EscapeXml(dto.Header?.RazonSocialEmisor ?? "Emisor");
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
