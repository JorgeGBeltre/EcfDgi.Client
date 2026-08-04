using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using EcfDgii.Client.Application.Documents.Dto;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Infrastructure.Persistence;
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

        public DocumentsController(
            ApplicationDbContext db,
            IEcfSequenceManager sequenceManager,
            IEcfClient ecfClient)
        {
            _db = db;
            _sequenceManager = sequenceManager;
            _ecfClient = ecfClient;
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

            // 3. Compute Security Code (6-char Base64 hash prefix)
            var secBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{eNcf}:{dto.Totals?.MontoTotal ?? 0}:{dto.Header?.RncEmisor}"));
            var secCode = Convert.ToBase64String(secBytes).Substring(0, 6).ToUpperInvariant();

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
                SecurityCode = secCode,
                XmlContent = rawXml,
                State = "SequenceAllocated"
            };

            _db.EcfDocuments.Add(doc);
            await _db.SaveChangesAsync();

            // 4. Mark Signed & Submit
            doc.SignedXmlContent = rawXml;
            doc.State = "Signed";
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
            sb.AppendLine($"<ECF xmlns=\"http://dgii.gov.do/ecf/messages/v1\">");
            sb.AppendLine($"  <Encabezado>");
            sb.AppendLine($"    <IdDoc><eNCF>{eNcf}</eNCF><TipoIngreso>01</TipoIngreso></IdDoc>");
            sb.AppendLine($"    <Emisor><RNCEmisor>{dto.Header?.RncEmisor}</RNCEmisor><RazonSocial>{dto.Header?.RazonSocialEmisor}</RazonSocial></Emisor>");
            sb.AppendLine($"    <Comprador><RNCComprador>{dto.Header?.RncComprador}</RNCComprador><RazonSocial>{dto.Header?.RazonSocialComprador}</RazonSocial></Comprador>");
            sb.AppendLine($"    <Totales><MontoTotal>{dto.Totals?.MontoTotal:F2}</MontoTotal><MontoItbis>{dto.Totals?.MontoItbis:F2}</MontoItbis></Totales>");
            sb.AppendLine($"  </Encabezado>");
            sb.AppendLine($"</ECF>");
            return sb.ToString();
        }
    }
}
