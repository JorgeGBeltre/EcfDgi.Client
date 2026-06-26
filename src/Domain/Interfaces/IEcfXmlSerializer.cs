namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IEcfXmlSerializer
    {
        string Serialize<T>(T model) where T : class;
        T Deserialize<T>(string xml) where T : class;
        string GetFileName(string rncEmisor, string eNcf);
        string EscapeAlfanum(string value);
    }
}
