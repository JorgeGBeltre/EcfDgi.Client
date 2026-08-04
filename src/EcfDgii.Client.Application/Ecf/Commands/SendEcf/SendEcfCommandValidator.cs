using FluentValidation;

namespace EcfDgii.Client.Application.Ecf.Commands.SendEcf
{
    public class SendEcfCommandValidator : AbstractValidator<SendEcfCommand>
    {
        public SendEcfCommandValidator()
        {
            RuleFor(v => v.XmlContent)
                .NotEmpty().WithMessage("XML Content is required.");

            RuleFor(v => v.FileName)
                .NotEmpty().WithMessage("File name is required.")
                .MaximumLength(150).WithMessage("File name must not exceed 150 characters.");

            RuleFor(v => v.RncEmisor)
                .NotEmpty().WithMessage("RNC Emisor is required.")
                .Matches(@"^\d{9}$|^\d{11}$").WithMessage("RNC Emisor must be exactly 9 or 11 numeric digits.");

            RuleFor(v => v.ENcf)
                .NotEmpty().WithMessage("e-NCF is required.")
                .MaximumLength(20).WithMessage("e-NCF must not exceed 20 characters.");

            RuleFor(v => v.TotalAmount)
                .GreaterThanOrEqualTo(0).WithMessage("Total amount must be greater than or equal to 0.");

            RuleFor(v => v.ItbisAmount)
                .GreaterThanOrEqualTo(0).WithMessage("ITBIS amount must be greater than or equal to 0.");
        }
    }
}
