using System.Threading;
using System.Threading.Tasks;

namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }
}
