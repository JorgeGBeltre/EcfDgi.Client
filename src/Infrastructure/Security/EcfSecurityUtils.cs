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
            var bytes = Encoding.UTF8.GetBytes(signatureValue);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 6);
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
            $"&codigoseuridad={Encode(req.CodigoSeguridad)}";

        public static string BuildTimbreFcUrl(string baseUrl, TimbreFcRequest req) =>
            $"{baseUrl}?rncemisor={Encode(req.RncEmisor)}" +
            $"&encf={Encode(req.ENcf)}" +
            $"&montototal={Encode(req.MontoTotal.ToString("F2", CultureInfo.InvariantCulture))}" +
            $"&codigoseuridad={Encode(req.CodigoSeguridad)}";

        private static string Encode(string value) =>
            Uri.EscapeDataString(value ?? string.Empty);
    }
}
