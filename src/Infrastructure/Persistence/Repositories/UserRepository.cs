using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Infrastructure.Persistence.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly ApplicationDbContext _dbContext;

        public UserRepository(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public Task<User?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            return _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        }

        public Task<User?> GetByUsernameAsync(string username, CancellationToken ct = default)
        {
            return _dbContext.Users.FirstOrDefaultAsync(u => u.Username == username, ct);
        }

        public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
        {
            return _dbContext.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        }

        public async Task AddAsync(User user, CancellationToken ct = default)
        {
            await _dbContext.Users.AddAsync(user, ct);
        }

        public void Update(User user)
        {
            _dbContext.Users.Update(user);
        }
    }
}
