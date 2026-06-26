using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

using EcfDgii.Client.Domain.Exceptions;

namespace EcfDgii.Client.Infrastructure.Security
{
    public class EcfXmlSigner
    {
        private readonly X509Certificate2 _certificate;

        public EcfXmlSigner(string pfxPath, string pfxPassword)
        {
            if (!File.Exists(pfxPath))
                throw new FileNotFoundException("Certificado no encontrado", pfxPath);

            _certificate = new X509Certificate2(pfxPath, pfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
        }

        public EcfXmlSigner(X509Certificate2 certificate)
        {
            _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
        }

        public string SignXml(string xmlContent, string rncEmisor)
        {
            if (!ValidateCertificateSn(rncEmisor))
                throw new EcfSigningException($"El RNC del certificado no coincide con el emisor: {rncEmisor}");

            var doc = new XmlDocument { PreserveWhitespace = false };
            doc.LoadXml(xmlContent);

            var signedXml = new SignedXml(doc);
            signedXml.SigningKey = _certificate.GetRSAPrivateKey();
            signedXml.SignedInfo.SignatureMethod = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";

            var reference = new Reference();
            reference.Uri = "";
            reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
            reference.AddTransform(new XmlDsigExcC14NTransform());
            reference.DigestMethod = "http://www.w3.org/2001/04/xmlenc#sha256";
            signedXml.AddReference(reference);

            var keyInfo = new KeyInfo();
            keyInfo.AddClause(new KeyInfoX509Data(_certificate));
            signedXml.KeyInfo = keyInfo;

            signedXml.ComputeSignature();
            var xmlDigitalSignature = signedXml.GetXml();

            doc.DocumentElement?.AppendChild(doc.ImportNode(xmlDigitalSignature, true));

            return doc.OuterXml;
        }

        public string ExtractSignatureValue(string signedXml)
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

        public bool ValidateCertificateSn(string rncOCedula)
        {
            return _certificate.Subject.Contains(rncOCedula);
        }
    }
}
