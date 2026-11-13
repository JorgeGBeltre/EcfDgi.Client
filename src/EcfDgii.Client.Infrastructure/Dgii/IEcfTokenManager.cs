using System.Threading;
using System.Threading.Tasks;

namespace EcfDgii.Client.Infrastructure.Dgii
{
    public interface IEcfTokenManager
    {
        Task<string> GetTokenAsync(CancellationToken ct = default);

        Task InvalidateAsync(CancellationToken ct = default);
    }
}
