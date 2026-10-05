using System;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Domain.Common
{
    public static class EcfEnvironmentHelper
    {
        public static AmbienteEnum ResolveAmbienteEnum(string? rawEnv, AmbienteEnum defaultAmbiente = AmbienteEnum.Certificacion)
        {
            if (string.IsNullOrWhiteSpace(rawEnv)) return defaultAmbiente;
            var lower = rawEnv.Trim().ToLowerInvariant();
            if (lower == "test" || lower == "testecf" || lower.Contains("precert")) return AmbienteEnum.PreCertificacion;
            if (lower == "cert" || lower == "certecf" || lower.Contains("certific") || lower.Contains("homolog")) return AmbienteEnum.Certificacion;
            if (lower == "prod" || lower == "ecf" || lower.Contains("producc") || lower.Contains("product")) return AmbienteEnum.Produccion;
            if (Enum.TryParse<AmbienteEnum>(rawEnv, true, out var parsed)) return parsed;
            return defaultAmbiente;
        }

        public static EcfEnvironment ToEcfEnvironment(AmbienteEnum ambiente) => ambiente switch
        {
            AmbienteEnum.PreCertificacion => EcfEnvironment.Test,
            AmbienteEnum.Certificacion => EcfEnvironment.Cert,
            AmbienteEnum.Produccion => EcfEnvironment.Prod,
            _ => EcfEnvironment.Cert
        };

        public static EcfEnvironment ResolveEcfEnvironment(string? rawEnv, EcfEnvironment defaultEnv = EcfEnvironment.Cert)
        {
            if (string.IsNullOrWhiteSpace(rawEnv)) return defaultEnv;
            var lower = rawEnv.Trim().ToLowerInvariant();
            if (lower == "test" || lower == "testecf" || lower.Contains("precert")) return EcfEnvironment.Test;
            if (lower == "cert" || lower == "certecf" || lower.Contains("certific") || lower.Contains("homolog")) return EcfEnvironment.Cert;
            if (lower == "prod" || lower == "ecf" || lower.Contains("producc") || lower.Contains("product")) return EcfEnvironment.Prod;
            if (Enum.TryParse<EcfEnvironment>(rawEnv, true, out var parsed)) return parsed;
            return defaultEnv;
        }

        public static bool IsProduction(string? rawEnv)
        {
            if (string.IsNullOrWhiteSpace(rawEnv)) return false;
            var lower = rawEnv.Trim().ToLowerInvariant();
            return lower == "prod" || lower == "ecf" || lower.Contains("producc") || lower.Contains("product");
        }
    }
}
