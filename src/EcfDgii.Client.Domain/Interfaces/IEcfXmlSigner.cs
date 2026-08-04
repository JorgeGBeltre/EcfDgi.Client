namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IEcfXmlSigner
    {
        string SignXml(string xmlContent, string rncEmisor);
        string ExtractSignatureValue(string signedXml);
        bool ValidateCertificateSn(string rncOCedula);
    }
}
