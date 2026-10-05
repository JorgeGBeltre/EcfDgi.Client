using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FluentValidation;
using EcfDgii.Client.Application.Documents.Dto;

namespace EcfDgii.Client.Application.Documents.Validators
{
    public class CanonicalDocumentDtoValidator : AbstractValidator<CanonicalDocumentDto>
    {
        private static readonly HashSet<string> ValidTipoComprobantes = new(StringComparer.OrdinalIgnoreCase)
        {
            "E31", "E32", "E33", "E34", "E41", "E43", "E44", "E45", "E46", "E47"
        };

        public CanonicalDocumentDtoValidator()
        {
            RuleFor(x => x.SourceReference)
                .NotNull().WithMessage("SourceReference is required.");

            When(x => x.SourceReference != null, () =>
            {
                RuleFor(x => x.SourceReference.TxnId)
                    .NotEmpty().WithMessage("SourceReference.TxnId is required.")
                    .MaximumLength(100).WithMessage("SourceReference.TxnId cannot exceed 100 characters.");

                RuleFor(x => x.SourceReference.EditSequence)
                    .MaximumLength(50).WithMessage("SourceReference.EditSequence cannot exceed 50 characters.");
            });

            RuleFor(x => x.TipoComprobante)
                .NotEmpty().WithMessage("TipoComprobante is required.")
                .Must(x => !string.IsNullOrWhiteSpace(x) && ValidTipoComprobantes.Contains(x.Trim()))
                .WithMessage(x => $"TipoComprobante '{x.TipoComprobante}' no es soportado. Valores admitidos: E31, E32, E33, E34, E41, E43, E44, E45, E46, E47.");

            When(x => !string.IsNullOrWhiteSpace(x.Header?.RncEmisor), () =>
            {
                RuleFor(x => x.Header!.RncEmisor)
                    .Must(rnc =>
                    {
                        var clean = Regex.Replace(rnc ?? "", @"[^\d]", "");
                        return clean.Length is 9 or 11;
                    })
                    .WithMessage(x => $"Header.RncEmisor '{x.Header?.RncEmisor}' es inválido. Debe tener 9 dígitos (RNC) u 11 dígitos (Cédula).");
            });

            When(x => x.Totals != null, () =>
            {
                RuleFor(x => x.Totals!.MontoSubtotal)
                    .GreaterThanOrEqualTo(0).WithMessage("Totals.MontoSubtotal cannot be negative.");

                RuleFor(x => x.Totals!.MontoItbis)
                    .GreaterThanOrEqualTo(0).WithMessage("Totals.MontoItbis cannot be negative.");

                RuleFor(x => x.Totals!.MontoTotal)
                    .GreaterThanOrEqualTo(0).WithMessage("Totals.MontoTotal cannot be negative.");

                // MED-105: Coherencia de montos declarados: MontoTotal == MontoSubtotal + MontoItbis
                RuleFor(x => x.Totals!)
                    .Must(t =>
                    {
                        var baseAmount = (t.MontoGravadoTotal ?? 0m) + (t.MontoExento ?? 0m);
                        if (baseAmount == 0m && t.MontoSubtotal > 0m) baseAmount = t.MontoSubtotal;
                        return baseAmount == 0m || Math.Abs(t.MontoTotal - (baseAmount + t.MontoItbis)) <= 0.05m;
                    })
                    .WithMessage(x => $"Discrepancia en totales: MontoTotal ({x.Totals!.MontoTotal}) debe coincidir con Subtotal + ITBIS.");
            });

            // MED-105 & MED-107: Líneas y descuentos coherentes
            When(x => x.Lines != null && x.Lines.Count > 0, () =>
            {
                RuleFor(x => x.Lines)
                    .Must(lines => lines[0].Amount >= 0 && lines[0].UnitPrice >= 0)
                    .WithMessage("Una línea de descuento no puede preceder al ítem que descuenta.");

                RuleFor(x => x.Lines)
                    .Must(lines => lines.Any(l => l.Amount > 0 || l.UnitPrice > 0))
                    .WithMessage("El comprobante no posee líneas facturables con importe mayor a cero.");
            });
        }
    }
}
