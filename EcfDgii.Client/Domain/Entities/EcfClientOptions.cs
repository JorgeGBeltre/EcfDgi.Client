using System;

namespace EcfDgii.Client.Domain.Entities
{
    public enum EcfEnvironment
    {
        Test,
        Cert,
        Prod
    }

    public enum IntegrationMode
    {
        DgiiDirect
    }

    public class EcfClientOptions
    {
        public string? ApiKey { get; set; }
        public string? BaseUrl { get; set; }
        public EcfEnvironment Environment { get; set; } = EcfEnvironment.Test;
        public IntegrationMode Mode { get; set; } = IntegrationMode.DgiiDirect;
        public string? RncEmisor { get; set; }
        public string? CertificatePath { get; set; }
        public string? CertificatePassword { get; set; }
        public bool AutoRetryOnReuseableSequence { get; set; } = true;
    }

    public class PollingOptions
    {
        public int InitialDelayMs { get; set; } = 1000;
        public int MaxDelayMs { get; set; } = 30000;
        public int MaxRetries { get; set; } = 60;
        public double BackoffMultiplier { get; set; } = 2;
        public int? TimeoutMs { get; set; }
    }
}
