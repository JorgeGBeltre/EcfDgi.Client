using FluentValidation;

namespace EcfDgii.Client.Application.Auth.Commands.Register
{
    public class RegisterUserCommandValidator : AbstractValidator<RegisterUserCommand>
    {
        public RegisterUserCommandValidator()
        {
            RuleFor(v => v.Username)
                .NotEmpty().WithMessage("Username is required.")
                .MaximumLength(100).WithMessage("Username must not exceed 100 characters.");

            RuleFor(v => v.Email)
                .NotEmpty().WithMessage("Email is required.")
                .EmailAddress().WithMessage("Email must be a valid email address.")
                .MaximumLength(150).WithMessage("Email must not exceed 150 characters.");

            RuleFor(v => v.Password)
                .NotEmpty().WithMessage("Password is required.")
                .MinimumLength(6).WithMessage("Password must be at least 6 characters long.");

            RuleFor(v => v.Role)
                .NotEmpty().WithMessage("Role is required.")
                .Must(role => role == "Admin" || role == "User").WithMessage("Role must be either 'Admin' or 'User'.");
        }
    }
}
