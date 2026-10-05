using System;
using System.ComponentModel;
using System.Globalization;
using EcfDgii.Client.Domain.Common;

namespace EcfDgii.Client.Domain.Entities
{
    [TypeConverter(typeof(EcfEnvironmentTypeConverter))]
    public enum EcfEnvironment
    {
        Test = 0,
        Cert = 1,
        Prod = 2,

        // Common aliases & Spanish equivalents for DGII integration & environment configs
        Produccion = Prod,
        Certificacion = Cert,
        PreCertificacion = Test,
        Production = Prod,
        Certification = Cert,
        Certecf = Cert,
        TestEcf = Test,
        Ecf = Prod
    }

    public class EcfEnvironmentTypeConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
        {
            return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
        }

        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if (value is string str)
            {
                return EcfEnvironmentHelper.ResolveEcfEnvironment(str, EcfEnvironment.Test);
            }
            return base.ConvertFrom(context, culture, value);
        }

        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType)
        {
            return destinationType == typeof(string) || base.CanConvertTo(context, destinationType);
        }

        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            if (destinationType == typeof(string) && value is EcfEnvironment env)
            {
                return env.ToString();
            }
            return base.ConvertTo(context, culture, value, destinationType);
        }
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
        public string? XsdDirectoryPath { get; set; }
        public bool ValidateSchemasLocal { get; set; } = true;
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
