using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using EcfDgii.Client.Domain.Entities;
using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Infrastructure.Persistence.Repositories
{
    public class CustomerRepository : ICustomerRepository
    {
        private readonly ApplicationDbContext _dbContext;

        public CustomerRepository(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default)
        {
            return _dbContext.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        }

        public Task<List<Customer>> GetAllAsync(CancellationToken ct = default)
        {
            return _dbContext.Customers.ToListAsync(ct);
        }

        public async Task AddAsync(Customer customer, CancellationToken ct = default)
        {
            await _dbContext.Customers.AddAsync(customer, ct);
        }

        public void Update(Customer customer)
        {
            _dbContext.Customers.Update(customer);
        }

        public void Delete(Customer customer)
        {
            _dbContext.Customers.Remove(customer); // This will be intercepted and soft deleted
        }
    }
}
