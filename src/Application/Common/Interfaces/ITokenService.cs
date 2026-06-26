using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Application.Common.Interfaces
{
    public interface ITokenService
    {
        string GenerateToken(User user);
    }
}
