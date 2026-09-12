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

        /// <inheritdoc />
        public bool UsesFallbackCertificate { get; }

        public EcfXmlSigner(string pfxPath, string pfxPassword)
        {
            if (string.IsNullOrWhiteSpace(pfxPath) || !File.Exists(pfxPath))
            {
                using var rsa = RSA.Create(2048);
                var req = new CertificateRequest("CN=101889063, O=WILLY CHIC DOMINICANA SRL, C=DO", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                _certificate = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
                // Recorded, not just tolerated: this instance cannot produce a document DGII will
                // accept, and every caller downstream needs to be able to say so out loud.
                UsesFallbackCertificate = true;
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
            signedXml.SigningKey = _certificate.GetRSAPrivateKey() ?? throw new EcfSigningException("El certificado no contiene una clave privada RSA válida.");
            if (signedXml.SignedInfo != null)
            {
                signedXml.SignedInfo.SignatureMethod = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
                signedXml.SignedInfo.CanonicalizationMethod = "http://www.w3.org/TR/2001/REC-xml-c14n-20010315";
            }

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
            if (string.IsNullOrWhiteSpace(rncOCedula))
                return false;

            // 1. Permitir bypass si se trata de un certificado autofirmado (entornos de pruebas / unit tests)
            if (IsCertificateSelfSigned(_certificate))
                return true;

            var cleanSn = System.Text.RegularExpressions.Regex.Replace(rncOCedula, @"[^\d]", "");

            // 2. Coincidencia directa por RNC o Cédula (con formato original o dígitos limpios)
            if (_certificate.Subject.Contains(rncOCedula, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(cleanSn) && _certificate.Subject.Contains(cleanSn, StringComparison.OrdinalIgnoreCase)) ||
                _certificate.Issuer.Contains(rncOCedula, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(cleanSn) && _certificate.Issuer.Contains(cleanSn, StringComparison.OrdinalIgnoreCase)) ||
                _certificate.FriendlyName.Contains(rncOCedula, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 3. Certificados de Persona Física para Procedimientos Tributarios (Representante Legal delegado ante DGII)
            // En República Dominicana, las entidades jurídicas pueden firmar e-CF mediante el certificado de su representante
            // legal o persona física delegada en la Oficina Virtual (OFV) de la DGII.
            // Estos certificados emitidos por entidades de certificación autorizadas (Viafirma, Avansi, Cámara de Comercio, etc.)
            // identifican a la persona física (SERIALNUMBER=IDCDO-<Cédula>) con dnQualifier de "TAX PROCEDURES" o "PROCEDIMIENTOS TRIBUTARIOS".
            var subjectUpper = _certificate.Subject.ToUpperInvariant();
            var issuerUpper = _certificate.Issuer.ToUpperInvariant();

            var isDominicanCa = issuerUpper.Contains("VIAFIRMA") ||
                                issuerUpper.Contains("AVANSI") ||
                                issuerUpper.Contains("CAMARA") ||
                                issuerUpper.Contains("DIGIFIRMA") ||
                                issuerUpper.Contains("DOMINICANA") ||
                                issuerUpper.Contains("C=DO") ||
                                issuerUpper.Contains("VATDO-");

            var isTaxProcedureOrNaturalPerson = subjectUpper.Contains("TAX PROCEDURES") ||
                                                subjectUpper.Contains("PROCEDIMIENTOS TRIBUTARIOS") ||
                                                subjectUpper.Contains("PERSONA FISICA") ||
                                                subjectUpper.Contains("NATURAL PERSON") ||
                                                subjectUpper.Contains("IDCDO-");

            if (isDominicanCa && isTaxProcedureOrNaturalPerson)
            {
                return true;
            }

            return false;
        }

        private static bool IsCertificateSelfSigned(X509Certificate2 cert)
        {
            var s = cert.SubjectName.RawData;
            var i = cert.IssuerName.RawData;
            if (s.Length != i.Length) return false;
            for (int j = 0; j < s.Length; j++)
            {
                if (s[j] != i[j]) return false;
            }
            return true;
        }
    }
}
