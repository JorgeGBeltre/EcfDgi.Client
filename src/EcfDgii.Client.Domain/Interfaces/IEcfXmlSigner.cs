namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IEcfXmlSigner
    {
        string SignXml(string xmlContent, string rncEmisor);
        string ExtractSignatureValue(string signedXml);
        bool ValidateCertificateSn(string rncOCedula);

        /// <summary>
        /// True when no real certificate was configured and one was self-generated to keep local
        /// development working. A document signed under those conditions can never be valid to DGII,
        /// and callers must be able to tell — otherwise it is indistinguishable from a real signature
        /// right up until DGII refuses it.
        ///
        /// False is the safe default for any implementation that always holds a real credential.
        /// </summary>
        bool UsesFallbackCertificate => false;
    }
}
