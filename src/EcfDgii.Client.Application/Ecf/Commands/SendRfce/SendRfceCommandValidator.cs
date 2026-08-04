using FluentValidation;

namespace EcfDgii.Client.Application.Ecf.Commands.SendRfce
{
    public class SendRfceCommandValidator : AbstractValidator<SendRfceCommand>
    {
        public SendRfceCommandValidator()
        {
            RuleFor(v => v.RfceModel)
                .NotNull().WithMessage("RfceModel is required.");

            RuleFor(v => v.RfceModel.Encabezado)
                .NotNull().WithMessage("RfceModel Encabezado is required.");

            RuleFor(v => v.RfceModel.Encabezado.Emisor)
                .NotNull().WithMessage("RfceModel Emisor details are required.");

            RuleFor(v => v.RfceModel.Encabezado.Emisor.RncEmisor)
                .NotEmpty().WithMessage("RncEmisor is required.")
                .Matches(@"^\d{9}$|^\d{11}$").WithMessage("RNC Emisor must be exactly 9 or 11 numeric digits.");

            RuleFor(v => v.RfceModel.Encabezado.IdDoc)
                .NotNull().WithMessage("RfceModel IdDoc details are required.");

            RuleFor(v => v.RfceModel.Encabezado.IdDoc.ENcf)
                .NotEmpty().WithMessage("e-NCF is required.")
                .MaximumLength(20).WithMessage("e-NCF must not exceed 20 characters.");
        }
    }
}
