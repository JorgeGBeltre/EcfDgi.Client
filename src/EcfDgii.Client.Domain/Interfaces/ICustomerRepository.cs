using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EcfDgii.Client.Domain.Entities;

namespace EcfDgii.Client.Domain.Interfaces
{
    public interface ICustomerRepository
    {
        Task<Customer?> GetByIdAsync(Guid id, CancellationToken ct = default);
        Task<List<Customer>> GetAllAsync(CancellationToken ct = default);
        Task AddAsync(Customer customer, CancellationToken ct = default);
        void Update(Customer customer);
        void Delete(Customer customer);
    }
}
