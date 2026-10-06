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
                var req = new CertificateRequest("CN=FALLBACK, O=FALLBACK CERTIFICATE, C=DO", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                _certificate = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
                // Recorded, not just tolerated: this instance cannot produce a document DGII will
                // accept, and every caller downstream needs to be able to say so out loud.
                UsesFallbackCertificate = true;
            }
            else
            {
                _certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, pfxPassword, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                UsesFallbackCertificate = false;
            }
        }

        public EcfXmlSigner(X509Certificate2 certificate)
        {
            _certificate = certificate ?? throw new ArgumentNullException(nameof(certificate));
            UsesFallbackCertificate = false;
        }

        public string SignXml(string xmlContent, string rncEmisor)
        {
            if (DateTime.UtcNow < _certificate.NotBefore || DateTime.UtcNow > _certificate.NotAfter)
            {
                throw new EcfSigningException($"El certificado digital ha expirado o aún no es válido (Vigencia: {_certificate.NotBefore:yyyy-MM-dd} a {_certificate.NotAfter:yyyy-MM-dd}).");
            }

            if (!ValidateCertificateSn(rncEmisor))
                throw new EcfSigningException($"El RNC del certificado no coincide con el emisor: {rncEmisor}");

            var doc = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
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
            var doc = new XmlDocument { XmlResolver = null };
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

            // 1. Si es certificado fallback/autofirmado sin identidad fiscal real, permitir firma local
            if (UsesFallbackCertificate)
                return true;

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

            // 3. Certificados cualificados de persona física para procedimientos tributarios (Viafirma / Avansi / DGII)
            // Bajo la normativa de la DGII (Norma General 06-2018, Ley 32-23), los contribuyentes (personas jurídicas con RNC de 9 dígitos)
            // delegan la firma de e-CF a una persona física autorizada (representante legal / contador) en la Oficina Virtual.
            // Dichos certificados contienen en el Subject la cédula del representante (SERIALNUMBER=IDCDO-XXXXXXXXXXX) y
            // dnQualifier indicando 'QUALIFIED CERTIFICATE FOR NATURAL PERSON - TAX PROCEDURES' (o equivalente en español).
            var isTaxProcedures = subject.Contains("TAX PROCEDURES", StringComparison.OrdinalIgnoreCase) ||
                                  subject.Contains("PROCEDIMIENTOS TRIBUTARIOS", StringComparison.OrdinalIgnoreCase);
            var isNaturalPerson = subject.Contains("NATURAL PERSON", StringComparison.OrdinalIgnoreCase) ||
                                  subject.Contains("PERSONA FISICA", StringComparison.OrdinalIgnoreCase);
            var isDominican = subject.Contains("C=DO", StringComparison.OrdinalIgnoreCase) ||
                              (_certificate.Issuer ?? string.Empty).Contains("C=DO", StringComparison.OrdinalIgnoreCase);

            if ((isTaxProcedures || isNaturalPerson) && isDominican)
            {
                // Si el certificado tiene un RNC corporativo explícito de 9 dígitos (ej. VATDO-133664692),
                // debe coincidir con cleanTarget. Si tiene un RNC corporativo diferente, no es válido para este target.
                var corporateMatches = System.Text.RegularExpressions.Regex.Matches(subject, @"(?<=(VATDO|RNC)[-:\s]?)(\d{9})\b");
                var hasExplicitOtherCorporateRnc = false;
                foreach (System.Text.RegularExpressions.Match cm in corporateMatches)
                {
                    if (cm.Success)
                    {
                        if (cm.Value == cleanTarget)
                            return true;
                        hasExplicitOtherCorporateRnc = true;
                    }
                }

                // Si no tiene un RNC corporativo diferente en el Subject, es un certificado de persona física
                // delegado para procedimientos tributarios con cédula dominicana (11 dígitos), válido para actuar
                // en representación del contribuyente.
                if (!hasExplicitOtherCorporateRnc && System.Text.RegularExpressions.Regex.IsMatch(subject, @"(IDCDO|SERIALNUMBER|CEDULA)[-:\s=]?\d{11}"))
                {
                    return true;
                }
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
