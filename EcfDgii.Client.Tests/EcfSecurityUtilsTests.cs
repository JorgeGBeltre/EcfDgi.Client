using System;
using EcfDgii.Client.Infrastructure.Security;
using EcfDgii.Client.Domain.Exceptions;
using Xunit;

namespace EcfDgii.Client.Tests
{
    public class EcfSecurityUtilsTests
    {
        [Fact]
        public void CalcularCodigoSeguridad_ValidSignedXml_Returns6CharHex()
        {
            // Minimal XML with SignatureValue
            string signedXml = @"<RFCE>
                <ds:Signature xmlns:ds=""http://www.w3.org/2000/09/xmldsig#"">
                    <ds:SignatureValue>dGVzdC1zaWduYXR1cmU=</ds:SignatureValue>
                </ds:Signature>
            </RFCE>";

            var code = EcfSecurityUtils.CalcularCodigoSeguridad(signedXml);

            Assert.Equal(6, code.Length);
            // Verify it's hex
            Assert.Matches("^[0-9a-f]{6}$", code);
        }

        [Fact]
        public void ExtractSignatureValue_NoSignature_ThrowsEcfException()
        {
            string xml = "<RFCE></RFCE>";
            Assert.Throws<EcfException>(() => EcfSecurityUtils.ExtractSignatureValue(xml));
        }
    }
}
