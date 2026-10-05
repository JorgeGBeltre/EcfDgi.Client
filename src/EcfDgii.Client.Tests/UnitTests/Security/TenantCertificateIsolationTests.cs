using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EcfDgii.Client.Domain.Exceptions;
using EcfDgii.Client.Infrastructure.Security;
using Xunit;

namespace EcfDgii.Client.UnitTests.Security
{
    public class TenantCertificateIsolationTests
    {
        [Fact]
        public void ValidateCertificateSn_ViafirmaTaxProceduresNaturalPerson_ReturnsTrueForCompanyRnc()
        {
            // Simular certificado de persona física delegada para procedimientos tributarios (Viafirma / DGII)
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "dnQualifier=QUALIFIED CERTIFICATE FOR NATURAL PERSON - TAX PROCEDURES, CN=JESSICA LARA BRAX, SERIALNUMBER=IDCDO-40220595868, OU=VATDO-133664692, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            // Debe validar positivamente para el RNC de la empresa emisora
            var isValid = signer.ValidateCertificateSn("133664692");
            Assert.True(isValid);
        }

        [Fact]
        public void ValidateCertificateSn_DirectRncMatch_ReturnsTrue()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=133664692, O=CERAMIC CHIC SRL, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            Assert.True(signer.ValidateCertificateSn("133664692"));
            Assert.True(signer.ValidateCertificateSn("133-66469-2"));
        }

        [Fact]
        public void ValidateCertificateSn_UnrelatedNonTaxCertificate_ReturnsFalse()
        {
            // Validar que RNC vacío siempre retorne false
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=Unrelated Organization, O=Foreign Corp, C=US",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);
            Assert.False(signer.ValidateCertificateSn(""));

            // Validar que un certificado emitido por CA extranjera que no coincida con el RNC retorne false
            using var caRsa = RSA.Create(2048);
            var caReq = new CertificateRequest("CN=Foreign Root CA, O=Foreign CA, C=US", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
            var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

            using var leafRsa = RSA.Create(2048);
            var leafReq = new CertificateRequest("CN=Foreign Person, O=Other Co, C=US", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var serial = new byte[] { 1, 2, 3, 4 };
            using var leafCert = leafReq.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
            using var leafCertWithKey = leafCert.CopyWithPrivateKey(leafRsa);

            var leafSigner = new EcfXmlSigner(leafCertWithKey);
            Assert.False(leafSigner.ValidateCertificateSn("133664692"));
        }

        [Fact]
        public void SignXml_WithTaxProceduresCertificate_SignsSuccessfully()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "dnQualifier=QUALIFIED CERTIFICATE FOR NATURAL PERSON - TAX PROCEDURES, CN=JESSICA LARA BRAX, SERIALNUMBER=IDCDO-40220595868, OU=VATDO-133664692, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            var sampleXml = "<ECF><Encabezado><Emisor><RNCEmisor>133664692</RNCEmisor></Emisor></Encabezado></ECF>";
            var signedXml = signer.SignXml(sampleXml, "133664692");

            Assert.NotNull(signedXml);
            Assert.Contains("SignatureValue", signedXml);
        }

        [Fact]
        public void ValidateCertificateSn_CrossTenantRnc_ReturnsFalse()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=133664692, O=CERAMIC CHIC SRL, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            // Debe rechazar un RNC diferente al del certificado
            Assert.False(signer.ValidateCertificateSn("101889063"));
            Assert.False(signer.ValidateCertificateSn("999999999"));
        }

        [Fact]
        public void SignXml_MismatchedRnc_ThrowsEcfSigningException()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=133664692, O=CERAMIC CHIC SRL, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            var sampleXml = "<ECF><Encabezado><Emisor><RNCEmisor>101889063</RNCEmisor></Emisor></Encabezado></ECF>";
            Assert.Throws<EcfSigningException>(() => signer.SignXml(sampleXml, "101889063"));
        }

        [Fact]
        public void ValidateCertificateSn_CaIssuedLeaf_StrictlyEnforcesSubjectRnc()
        {
            // Crear CA raíz
            using var caRsa = RSA.Create(2048);
            var caReq = new CertificateRequest("CN=Autoridad Certificadora Dominicana, O=Viafirma, C=DO", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
            var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

            // Crear certificado subordinado emitido por la CA para la empresa A (133664692)
            using var leafRsa = RSA.Create(2048);
            var leafReq = new CertificateRequest(
                "dnQualifier=QUALIFIED CERTIFICATE FOR NATURAL PERSON - TAX PROCEDURES, CN=JESSICA LARA BRAX, SERIALNUMBER=IDCDO-40220595868, OU=VATDO-133664692, C=DO",
                leafRsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var serial = new byte[] { 10, 20, 30, 40 };
            using var leafCert = leafReq.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
            using var leafWithKey = leafCert.CopyWithPrivateKey(leafRsa);

            var leafSigner = new EcfXmlSigner(leafWithKey);

            // Debe aceptar su propio RNC delegado
            Assert.True(leafSigner.ValidateCertificateSn("133664692"));
            // Debe rechazar el RNC de cualquier otro contribuyente
            Assert.False(leafSigner.ValidateCertificateSn("101889063"));
            Assert.False(leafSigner.ValidateCertificateSn("40220595869"));
        }
    }
}
