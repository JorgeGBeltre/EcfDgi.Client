using FluentValidation;

namespace EcfDgii.Client.Application.Customers.Commands.UpdateCustomer
{
    public class UpdateCustomerCommandValidator : AbstractValidator<UpdateCustomerCommand>
    {
        public UpdateCustomerCommandValidator()
        {
            RuleFor(v => v.Id)
                .NotEmpty().WithMessage("Customer ID is required.");

            RuleFor(v => v.Name)
                .NotEmpty().WithMessage("Name is required.")
                .MaximumLength(200).WithMessage("Name must not exceed 200 characters.");

            RuleFor(v => v.Email)
                .NotEmpty().WithMessage("Email is required.")
                .EmailAddress().WithMessage("Email must be a valid email address.")
                .MaximumLength(150).WithMessage("Email must not exceed 150 characters.");

            RuleFor(v => v.Rnc)
                .NotEmpty().WithMessage("RNC is required.")
                .Matches(@"^\d{9}$|^\d{11}$").WithMessage("RNC must be exactly 9 or 11 numeric digits.");
        }
    }
}
