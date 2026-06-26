using System.Threading;
using System.Threading.Tasks;
using MediatR;
using EcfDgii.Client.Application.Auth.Common;
using EcfDgii.Client.Application.Common.Interfaces;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;
using EcfDgii.Client.Shared.Common;

namespace EcfDgii.Client.Application.Auth.Commands.Register
{
    public class RegisterUserCommandHandler : IRequestHandler<RegisterUserCommand, Result<AuthResponseDto>>
    {
        private readonly IUserRepository _userRepository;
        private readonly IPasswordHasher _passwordHasher;
        private readonly ITokenService _tokenService;
        private readonly IUnitOfWork _unitOfWork;

        public RegisterUserCommandHandler(
            IUserRepository userRepository,
            IPasswordHasher passwordHasher,
            ITokenService tokenService,
            IUnitOfWork unitOfWork)
        {
            _userRepository = userRepository;
            _passwordHasher = passwordHasher;
            _tokenService = tokenService;
            _unitOfWork = unitOfWork;
        }

        public async Task<Result<AuthResponseDto>> Handle(RegisterUserCommand request, CancellationToken cancellationToken)
        {
            var existingUser = await _userRepository.GetByUsernameAsync(request.Username, cancellationToken);
            if (existingUser != null)
            {
                return Result<AuthResponseDto>.Failure("Username is already taken.");
            }

            var existingEmail = await _userRepository.GetByEmailAsync(request.Email, cancellationToken);
            if (existingEmail != null)
            {
                return Result<AuthResponseDto>.Failure("Email is already registered.");
            }

            var user = new User
            {
                Username = request.Username,
                Email = request.Email,
                PasswordHash = _passwordHasher.HashPassword(request.Password),
                Role = request.Role
            };

            await _userRepository.AddAsync(user, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);

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
