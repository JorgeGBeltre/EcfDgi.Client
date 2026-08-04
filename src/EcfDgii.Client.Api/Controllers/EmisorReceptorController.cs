using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using EcfDgii.Client.Domain.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EcfDgii.Client.Api.Controllers
{
    [ApiController]
    public class EmisorReceptorController : ControllerBase
    {
        private readonly IEcfXmlSigner _signer;

        public EmisorReceptorController(IEcfXmlSigner signer)
        {
            _signer = signer;
        }

        [HttpPost("fe/recepcion/api/ecf")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> RecepcioneCF(IFormFile xml)
        {
            if (xml == null || xml.Length == 0)
            {
                return BadRequest("Archivo XML no provisto o vacío.");
            }

            try
            {
                using var reader = new StreamReader(xml.OpenReadStream(), Encoding.UTF8);
                var xmlContent = await reader.ReadToEndAsync();

                // Parse XML to extract rncemisor, rnccomprador, encf
                var doc = new XmlDocument();
                doc.PreserveWhitespace = true;
                doc.LoadXml(xmlContent);

                var ns = new XmlNamespaceManager(doc.NameTable);
                var rncEmisor = doc.SelectSingleNode("//RNCEmisor", ns)?.InnerText?.Trim() ?? string.Empty;
                var rncComprador = doc.SelectSingleNode("//RNCComprador", ns)?.InnerText?.Trim() ?? string.Empty;
                var encf = doc.SelectSingleNode("//eNCF", ns)?.InnerText?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(rncEmisor) || string.IsNullOrEmpty(encf))
                {
                    return BadRequest("El XML de e-CF provisto no contiene las etiquetas obligatorias RNCEmisor o eNCF.");
                }

                // Build ARECF (Acuse de Recibo) XML string
                var arecfBuilder = new StringBuilder();
                arecfBuilder.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
                arecfBuilder.AppendLine("<arecf>");
                arecfBuilder.AppendLine("  <detalleacusederecibo>");
                arecfBuilder.AppendLine("    <version>1.0</version>");
                arecfBuilder.AppendLine($"    <rncemisor>{rncEmisor}</rncemisor>");
                arecfBuilder.AppendLine($"    <rnccomprador>{rncComprador}</rnccomprador>");
                arecfBuilder.AppendLine($"    <encf>{encf}</encf>");
                arecfBuilder.AppendLine("    <estado>0</estado>"); // 0 = Aceptado/Recibido
                var fechaHora = DateTime.Now.ToString("dd-MM-yyyy HH:mm:ss");
                arecfBuilder.AppendLine($"    <fechahoraacuserecibo>{fechaHora}</fechahoraacuserecibo>");
                arecfBuilder.AppendLine("  </detalleacusederecibo>");
                arecfBuilder.AppendLine("</arecf>");

                var unsignedArecf = arecfBuilder.ToString();
                
                // Sign the ARECF XML using our signer certificate (acting as comprador/receptor)
                var signedArecf = _signer.SignXml(unsignedArecf, rncComprador);

                return Content(signedArecf, "application/xml", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return BadRequest($"Error al procesar la recepción de e-CF: {ex.Message}");
            }
        }

        [HttpPost("fe/aprobacioncomercial/api/ecf")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> AprobacionComercial(IFormFile xml)
        {
            if (xml == null || xml.Length == 0)
            {
                return BadRequest("Archivo XML de aprobación comercial no provisto o vacío.");
            }

            try
            {
                using var reader = new StreamReader(xml.OpenReadStream(), Encoding.UTF8);
                var xmlContent = await reader.ReadToEndAsync();

                // Validate it parses as XML
                var doc = new XmlDocument();
                doc.LoadXml(xmlContent);

                return Ok();
            }
            catch (Exception ex)
            {
                return BadRequest($"Error al procesar la aprobación comercial: {ex.Message}");
            }
        }

        [HttpGet("fe/autenticacion/api/semilla")]
        public IActionResult GetSemilla()
        {
            var seedVal = Guid.NewGuid().ToString();
            var fecha = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz");
            var xml = $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                      $"<SemillaModel xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
                      $"  <valor>{seedVal}</valor>\n" +
                      $"  <fecha>{fecha}</fecha>\n" +
                      $"</SemillaModel>";

            return Content(xml, "application/xml", Encoding.UTF8);
        }

        [HttpPost("fe/autenticacion/api/validacioncertificado")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> ValidarSemilla(IFormFile xml)
        {
            if (xml == null || xml.Length == 0)
            {
                return BadRequest("Archivo XML de semilla firmada no provisto.");
            }

            try
            {
                using var reader = new StreamReader(xml.OpenReadStream(), Encoding.UTF8);
                var xmlContent = await reader.ReadToEndAsync();

                var doc = new XmlDocument();
                doc.LoadXml(xmlContent);

                var token = Guid.NewGuid().ToString().Replace("-", "");
                var expira = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
                var expedido = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

                if (Request.Headers.Accept.ToString().Contains("application/json"))
                {
                    return Ok(new { token, expira, expedido });
                }

                var xmlResponse = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                                  $"<RespuestaAutenticacion>\n" +
                                  $"  <token>{token}</token>\n" +
                                  $"  <expira>{expira}</expira>\n" +
                                  $"  <expedido>{expedido}</expedido>\n" +
                                  $"</RespuestaAutenticacion>";

                return Content(xmlResponse, "application/xml", Encoding.UTF8);
            }
            catch (Exception ex)
            {
                return BadRequest($"Fallo en validación de certificado: {ex.Message}");
            }
        }
    }
}
