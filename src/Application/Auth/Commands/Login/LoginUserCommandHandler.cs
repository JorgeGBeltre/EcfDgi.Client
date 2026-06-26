using System.Threading;
using System.Threading.Tasks;
using MediatR;
using EcfDgii.Client.Application.Auth.Common;
using EcfDgii.Client.Application.Common.Interfaces;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Auth.Commands.Login
{
    public class LoginUserCommandHandler : IRequestHandler<LoginUserCommand, Result<AuthResponseDto>>
    {
        private readonly IUserRepository _userRepository;
        private readonly IPasswordHasher _passwordHasher;
        private readonly ITokenService _tokenService;

        public LoginUserCommandHandler(
            IUserRepository userRepository,
            IPasswordHasher passwordHasher,
            ITokenService tokenService)
        {
            _userRepository = userRepository;
            _passwordHasher = passwordHasher;
            _tokenService = tokenService;
        }

        public async Task<Result<AuthResponseDto>> Handle(LoginUserCommand request, CancellationToken cancellationToken)
        {
            var user = await _userRepository.GetByUsernameAsync(request.Username, cancellationToken);
            if (user == null)
            {
                return Result<AuthResponseDto>.Failure("Invalid username or password.");
            }

            var isPasswordValid = _passwordHasher.VerifyPassword(request.Password, user.PasswordHash);
            if (!isPasswordValid)
            {
                return Result<AuthResponseDto>.Failure("Invalid username or password.");
            }

            var token = _tokenService.GenerateToken(user);

            var response = new AuthResponseDto
            {
                Username = user.Username,
                Role = user.Role,
                Token = token
            };

            return Result<AuthResponseDto>.Success(response);
        }
    }
}
