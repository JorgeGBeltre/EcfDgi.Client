namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IEcfXmlSigner : System.IDisposable
    {
        string SignXml(string xmlContent, string rncEmisor);
        string ExtractSignatureValue(string signedXml);
        bool ValidateCertificateSn(string rncOCedula);

        bool UsesFallbackCertificate => false;

        void System.IDisposable.Dispose() { }
    }
}
