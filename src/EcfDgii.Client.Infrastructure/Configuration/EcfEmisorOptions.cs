namespace EcfDgii.Client.Infrastructure.Configuration
{
    public class EcfEmisorOptions
    {
        public const string SectionName = "EcfEmisor";

        public string Rnc { get; set; } = string.Empty;

        public string RazonSocial { get; set; } = string.Empty;

        public string Direccion { get; set; } = string.Empty;
    }
}
