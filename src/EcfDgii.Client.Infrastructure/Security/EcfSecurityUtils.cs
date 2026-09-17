using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Exceptions;

namespace EcfDgii.Client.Infrastructure.Security
{
    public static class EcfSecurityUtils
    {
        public static string CalcularCodigoSeguridad(string signedXml)
        {
            var signatureValue = ExtractSignatureValue(signedXml);
            if (string.IsNullOrWhiteSpace(signatureValue) || signatureValue.Length < 6)
                throw new EcfException("El SignatureValue es inválido o tiene menos de 6 caracteres.");
            return signatureValue.Substring(0, 6);
        }

        public static string? ExtractFechaHoraFirma(string signedXml)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(signedXml);
                var node = doc.SelectSingleNode("//FechaHoraFirma");
                return node?.InnerText?.Trim();
            }
            catch
            {
                return null;
            }
        }

        public static string ExtractSignatureValue(string signedXml)
        {
            var doc = new XmlDocument();
            doc.LoadXml(signedXml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("ds", "http://www.w3.org/2000/09/xmldsig#");
            var node = doc.SelectSingleNode("//ds:SignatureValue", ns);
            if (node == null)
                throw new EcfException("El XML no contiene un nodo SignatureValue. ¿Fue firmado correctamente?");
            return node.InnerText.Trim();
        }

        public static string BuildTimbreUrl(string baseUrl, TimbreEcfRequest req) =>
            $"{baseUrl}?rncemisor={Encode(req.RncEmisor)}" +
            $"&rnccomprador={Encode(req.RncComprador ?? "")}" +
            $"&encf={Encode(req.ENcf)}" +
            $"&fechaemision={Encode(req.FechaEmision)}" +
            $"&montototal={Encode(req.MontoTotal.ToString("F2", CultureInfo.InvariantCulture))}" +
            $"&fechafirma={Encode(req.FechaFirma)}" +
            $"&codigoseguridad={Encode(req.CodigoSeguridad)}";

        public static string BuildTimbreFcUrl(string baseUrl, TimbreFcRequest req) =>
            $"{baseUrl}?rncemisor={Encode(req.RncEmisor)}" +
            $"&encf={Encode(req.ENcf)}" +
            $"&montototal={Encode(req.MontoTotal.ToString("F2", CultureInfo.InvariantCulture))}" +
            $"&codigoseguridad={Encode(req.CodigoSeguridad)}";

        private static string Encode(string value) =>
            Uri.EscapeDataString(value ?? string.Empty);
    }
}
