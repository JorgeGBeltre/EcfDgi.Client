using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Application.Services
{
    public class EcfValidator
    {
        private const decimal RfceThreshold = 250_000.00m;

        public ValidationResult ValidateRfce(Rfce rfce)
        {
            var errors = new List<string>();

            if (rfce == null)
            {
                errors.Add("El objeto RFCE no puede ser nulo.");
                return new ValidationResult(errors);
            }

            if (!IsValidRnc(rfce.Encabezado.Emisor.RncEmisor))
                errors.Add("RNCEmisor inválido: debe tener 9 u 11 dígitos numéricos.");

            if (rfce.Encabezado.IdDoc.ENcf?.Length != 13)
                errors.Add("eNCF inválido: debe tener exactamente 13 caracteres.");

            if (rfce.Encabezado.IdDoc.TipoeCF != "32")
                errors.Add("TipoeCF debe ser 32 para RFCE.");

            if (!DateTime.TryParseExact(rfce.Encabezado.Emisor.FechaEmision,
                "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                errors.Add("FechaEmision inválida: formato requerido dd-MM-AAAA.");

            if (rfce.Encabezado.Totales.MontoTotal < 0)
                errors.Add("MontoTotal no puede ser negativo.");

            if (rfce.Encabezado.Totales.MontoTotal >= RfceThreshold)
                errors.Add($"MontoTotal >= {RfceThreshold:N2}: usar recepcion (e-CF completo), no recepcionfc.");

            var comprador = rfce.Encabezado.Comprador;
            if (comprador != null &&
                !string.IsNullOrEmpty(comprador.RncComprador) &&
                !string.IsNullOrEmpty(comprador.IdentificadorExtranjero))
                errors.Add("RNCComprador e IdentificadorExtranjero son mutuamente excluyentes.");

            ValidateTotalesConsistency(rfce.Encabezado.Totales, errors);

            if (rfce.Encabezado.IdDoc.TablaFormasPago?.Count > 7)
                errors.Add("TablaFormasPago no puede tener más de 7 items.");

            if (rfce.Encabezado.Totales.ImpuestosAdicionales?.Count > 20)
                errors.Add("ImpuestosAdicionales no puede tener más de 20 items.");

            return new ValidationResult(errors);
        }

        private void ValidateTotalesConsistency(RfceTotales t, List<string> errors)
        {

            if (t.MontoGravadoTotal.HasValue)
            {
                var expected = (t.MontoGravadoI1 ?? 0) + (t.MontoGravadoI2 ?? 0) + (t.MontoGravadoI3 ?? 0);
                if (Math.Abs(t.MontoGravadoTotal.Value - expected) > 0.01m)
                    errors.Add("MontoGravadoTotal no coincide con la suma de MontoGravadoI1+I2+I3.");
            }

            if (t.TotalITBIS.HasValue)
            {
                var expected = (t.TotalITBIS1 ?? 0) + (t.TotalITBIS2 ?? 0) + (t.TotalITBIS3 ?? 0);
                if (Math.Abs(t.TotalITBIS.Value - expected) > 0.01m)
                    errors.Add("TotalITBIS no coincide con la suma de TotalITBIS1+2+3.");
            }

            if (t.MontoPeriodo.HasValue && t.MontoNoFacturable.HasValue)
            {
                var expected = t.MontoTotal + t.MontoNoFacturable.Value;
                if (Math.Abs(t.MontoPeriodo.Value - expected) > 0.01m)
                    errors.Add("MontoPeriodo debe ser igual a MontoTotal + MontoNoFacturable.");
            }
        }

        private bool IsValidRnc(string rnc) =>
            !string.IsNullOrEmpty(rnc) &&
            (rnc.Length == 9 || rnc.Length == 11) &&
            rnc.All(char.IsDigit);
    }

    public class ValidationResult
    {
        public bool IsValid => !Errors.Any();
        public IReadOnlyList<string> Errors { get; }
        public ValidationResult(List<string> errors) => Errors = errors;
    }

    public class EcfValidationException : Exception
    {
        public EcfValidationException(IEnumerable<string> errors)
            : base($"Errores de validación: {string.Join(", ", errors)}")
        {
            Errors = errors.ToList();
        }
        public List<string> Errors { get; }
    }
}
