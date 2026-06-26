using FluentValidation;

namespace EcfDgii.Client.Application.Auth.Commands.Login
{
    public class LoginUserCommandValidator : AbstractValidator<LoginUserCommand>
    {
        public LoginUserCommandValidator()
        {
            RuleFor(v => v.Username)
                .NotEmpty().WithMessage("Username is required.");

            RuleFor(v => v.Password)
                .NotEmpty().WithMessage("Password is required.");
        }
    }
}
