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
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "dnQualifier=QUALIFIED CERTIFICATE FOR NATURAL PERSON - TAX PROCEDURES, CN=MARIA PEREZ, SERIALNUMBER=IDCDO-00100000011, OU=VATDO-130000002, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            var isValid = signer.ValidateCertificateSn("130000002");
            Assert.True(isValid);
        }

        [Fact]
        public void ValidateCertificateSn_DirectRncMatch_ReturnsTrue()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=130000002, O=COMERCIAL EJEMPLO SRL, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            Assert.True(signer.ValidateCertificateSn("130000002"));
            Assert.True(signer.ValidateCertificateSn("130-00000-2"));
        }

        [Fact]
        public void ValidateCertificateSn_UnrelatedNonTaxCertificate_ReturnsFalse()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=Unrelated Organization, O=Foreign Corp, C=US",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);
            Assert.False(signer.ValidateCertificateSn(""));

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
            Assert.False(leafSigner.ValidateCertificateSn("130000002"));
        }

        [Fact]
        public void SignXml_WithTaxProceduresCertificate_SignsSuccessfully()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "dnQualifier=QUALIFIED CERTIFICATE FOR NATURAL PERSON - TAX PROCEDURES, CN=MARIA PEREZ, SERIALNUMBER=IDCDO-00100000011, OU=VATDO-130000002, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            var sampleXml = "<ECF><Encabezado><Emisor><RNCEmisor>130000002</RNCEmisor></Emisor></Encabezado></ECF>";
            var signedXml = signer.SignXml(sampleXml, "130000002");

            Assert.NotNull(signedXml);
            Assert.Contains("SignatureValue", signedXml);
        }

        [Fact]
        public void ValidateCertificateSn_CrossTenantRnc_ReturnsFalse()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=130000002, O=COMERCIAL EJEMPLO SRL, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            Assert.False(signer.ValidateCertificateSn("101000001"));
            Assert.False(signer.ValidateCertificateSn("999999999"));
        }

        [Fact]
        public void SignXml_MismatchedRnc_ThrowsEcfSigningException()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "CN=130000002, O=COMERCIAL EJEMPLO SRL, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            var sampleXml = "<ECF><Encabezado><Emisor><RNCEmisor>101000001</RNCEmisor></Emisor></Encabezado></ECF>";
            Assert.Throws<EcfSigningException>(() => signer.SignXml(sampleXml, "101000001"));
        }

        [Fact]
        public void ValidateCertificateSn_CaIssuedLeaf_StrictlyEnforcesSubjectRnc()
        {
            using var caRsa = RSA.Create(2048);
            var caReq = new CertificateRequest("CN=Autoridad Certificadora Dominicana, O=Viafirma, C=DO", caRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
            var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

            using var leafRsa = RSA.Create(2048);
            var leafReq = new CertificateRequest(
                "dnQualifier=QUALIFIED CERTIFICATE FOR NATURAL PERSON - TAX PROCEDURES, CN=MARIA PEREZ, SERIALNUMBER=IDCDO-00100000011, OU=VATDO-130000002, C=DO",
                leafRsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var serial = new byte[] { 10, 20, 30, 40 };
            using var leafCert = leafReq.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serial);
            using var leafWithKey = leafCert.CopyWithPrivateKey(leafRsa);

            var leafSigner = new EcfXmlSigner(leafWithKey);

            Assert.True(leafSigner.ValidateCertificateSn("130000002"));
            Assert.False(leafSigner.ValidateCertificateSn("101000001"));
            Assert.False(leafSigner.ValidateCertificateSn("00100000022"));
        }

        [Fact]
        public void ValidateCertificateSn_NaturalPersonTaxProceduresWithoutCorporateRnc_ReturnsTrueForCompanyRnc()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest(
                "dnQualifier=QUALIFIED CERTIFICATE FOR NATURAL PERSON - TAX PROCEDURES, CN=MARIA PEREZ DEMO, SERIALNUMBER=IDCDO-00100000011, G=MARIA, SN=PEREZ DEMO, C=DO",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            var signer = new EcfXmlSigner(cert);

            Assert.True(signer.ValidateCertificateSn("101000001"));
            Assert.True(signer.ValidateCertificateSn("130000002"));
            Assert.True(signer.ValidateCertificateSn("00100000011"));
            Assert.False(signer.ValidateCertificateSn(""));
        }
    }
}
