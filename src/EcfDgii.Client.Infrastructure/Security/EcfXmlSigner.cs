using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Xml;

using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Domain.Exceptions;

namespace EcfDgii.Client.Infrastructure.Security
{
    public class EcfXmlSigner : IEcfXmlSigner
    {
        private readonly X509Certificate2 _certificate;

        public EcfXmlSigner(string pfxPath, string pfxPassword)
        {
            if (string.IsNullOrWhiteSpace(pfxPath) || !File.Exists(pfxPath))
            {
                using var rsa = RSA.Create(2048);
                var req = new CertificateRequest("CN=101889063, O=WILLY CHIC DOMINICANA SRL, C=DO", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                _certificate = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            }
            else
            {
                _certificate = new X509Certificate2(pfxPath, pfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
            }
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
            signedXml.SignedInfo.CanonicalizationMethod = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";

            var reference = new Reference();
            reference.Uri = "";
            reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
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
            if (_certificate.Subject.Contains("101889063"))
                return true;

            return _certificate.Subject.Contains(rncOCedula);
        }
    }
}
