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
    public class EcfXmlSigner : IEcfXmlSigner, IDisposable
    {
        private readonly X509Certificate2 _certificate;
        private bool _disposed;

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
                UsesFallbackCertificate = IsCertificateSelfSigned(_certificate);
            }
        }

        public EcfXmlSigner(X509Certificate2 certificate)
        {
            _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
            UsesFallbackCertificate = IsCertificateSelfSigned(_certificate);
        }

        public string SignXml(string xmlContent, string rncEmisor)
        {
            if (DateTime.UtcNow < _certificate.NotBefore || DateTime.UtcNow > _certificate.NotAfter)
            {
                throw new EcfSigningException($"El certificado digital ha expirado o aún no es válido (Vigencia: {_certificate.NotBefore:yyyy-MM-dd} a {_certificate.NotAfter:yyyy-MM-dd}).");
            }

            if (!ValidateCertificateSn(rncEmisor))
                throw new EcfSigningException($"El RNC del certificado no coincide con el emisor: {rncEmisor}");

            var doc = new XmlDocument { PreserveWhitespace = false };
            doc.LoadXml(xmlContent);

            var signedXml = new SignedXml(doc);
            using var rsa = _certificate.GetRSAPrivateKey() ?? throw new EcfSigningException("El certificado no contiene una clave privada RSA válida.");
            signedXml.SigningKey = rsa;
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

            var cleanTarget = System.Text.RegularExpressions.Regex.Replace(rncOCedula, @"[^\d]", "");
            if (string.IsNullOrEmpty(cleanTarget))
                return false;

            // 2. Coincidencia estructurada en Subject (RNC o Cédula)
            // Extraer identificadores del Subject del certificado:
            // - SERIALNUMBER (OID 2.5.4.5): ej. IDCDO-00100000001
            // - organizationIdentifier (OID 2.5.4.97): ej. VATDO-101000001
            // - Cédula / RNC embebido en CN o Subject
            var subject = _certificate.Subject ?? string.Empty;
            
            // Buscar coincidencias exactas de dígitos del RNC/Cédula dentro del Subject
            var matches = System.Text.RegularExpressions.Regex.Matches(subject, @"(?<=(IDCDO|VATDO|RNC|CEDULA)?[-:\s]?)(\d{9,11})");
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Success && match.Value == cleanTarget)
                {
                    return true;
                }
            }

            // También verificar en Subject Alternative Names (SAN) si existen
            try
            {
                foreach (var ext in _certificate.Extensions)
                {
                    if (ext.Oid?.Value == "2.5.29.17") // Subject Alternative Name
                    {
                        var sanText = ext.Format(false);
                        var sanMatches = System.Text.RegularExpressions.Regex.Matches(sanText, @"\d{9,11}");
                        foreach (System.Text.RegularExpressions.Match sm in sanMatches)
                        {
                            if (sm.Success && sm.Value == cleanTarget)
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignorar error al leer extensiones
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

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _certificate.Dispose();
                GC.SuppressFinalize(this);
            }
        }
    }
}
