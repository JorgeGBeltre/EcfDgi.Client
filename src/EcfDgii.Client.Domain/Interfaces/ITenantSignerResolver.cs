using System.Threading;
using System.Threading.Tasks;

namespace EcfDgii.Client.Domain.Interfaces
{
    public interface ITenantSignerResolver
    {
        Task<IEcfXmlSigner> ResolveSignerAsync(string? rnc, CancellationToken ct = default);
    }
}
