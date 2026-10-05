namespace EcfDgii.Client.Shared.Common
{
    using System;
    using System.Collections.Generic;
    using System.Linq;

    /// <summary>
    /// Normalizador centralizado de secuencias NCF (Norma General 06-2018 y Norma General 07-2006 de la DGII).
    /// </summary>
    public static class NcfNormalizer
    {
        public const string LegacyPrefix = "B";
        public const string ElectronicPrefix = "E";

        public const string TipoCreditoFiscal = "01";
        public const string TipoConsumo = "02";
        public const string TipoNotaDebito = "03";
        public const string TipoNotaCredito = "04";

        /// <summary>
        /// Normaliza un identificador de comprobante al formato DGII oficial de 11 caracteres (legacy) o 13 caracteres (e-CF).
        /// </summary>
        public static string Normalize(string? input, string defaultType = TipoConsumo)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var clean = input.Trim().ToUpperInvariant();

            if (clean.StartsWith(ElectronicPrefix) && clean.Length == 13)
            {
                return clean;
            }

            if (clean.StartsWith(LegacyPrefix) && clean.Length == 11)
            {
                return clean;
            }

            if (clean.Length == 9 && clean.All(char.IsDigit) && (clean.StartsWith("1") || clean.StartsWith("2") || clean.StartsWith("4")))
            {
                return $"{LegacyPrefix}0{clean}";
            }

            if (clean.Length == 10 && clean.StartsWith("0") && clean.All(char.IsDigit))
            {
                return $"{LegacyPrefix}{clean}";
            }

            if (clean.Length == 8 && clean.All(char.IsDigit))
            {
                var typeCode = defaultType.PadLeft(2, '0');
                return $"{LegacyPrefix}{typeCode}{clean}";
            }

            return clean;
        }

        public static HashSet<string> GetLookupCandidates(string? input)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(input)) return candidates;

            var clean = input.Trim();
            candidates.Add(clean);

            if (clean.StartsWith("B0", StringComparison.OrdinalIgnoreCase) && clean.Length > 2)
            {
                candidates.Add(clean[2..]);
            }

            if (clean.StartsWith("B", StringComparison.OrdinalIgnoreCase) && clean.Length > 1)
            {
                candidates.Add(clean[1..]);
            }

            if (clean.Length == 9 && (clean.StartsWith("1") || clean.StartsWith("2") || clean.StartsWith("4")))
            {
                candidates.Add($"{LegacyPrefix}0{clean}");
            }

            if (clean.Length == 10 && clean.StartsWith("0"))
            {
                candidates.Add($"{LegacyPrefix}{clean}");
            }

            if (clean.Length == 8 && clean.All(char.IsDigit))
            {
                candidates.Add($"{LegacyPrefix}{TipoConsumo}{clean}");
                candidates.Add($"{LegacyPrefix}{TipoCreditoFiscal}{clean}");
            }

            return candidates;
        }

        public static bool IsElectronicNcf(string? input) =>
            !string.IsNullOrWhiteSpace(input) &&
            input.Trim().Length == 13 &&
            input.Trim().StartsWith(ElectronicPrefix, StringComparison.OrdinalIgnoreCase);

        public static bool IsLegacyNcf(string? input) =>
            !string.IsNullOrWhiteSpace(input) &&
            input.Trim().Length == 11 &&
            input.Trim().StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
