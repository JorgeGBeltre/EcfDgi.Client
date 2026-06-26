using System;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IUserRepository
    {
        Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default);
        Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
        Task AddAsync(User user, CancellationToken ct = default);
        void Update(User user);
    }
}
