using System.Threading;
using System.Threading.Tasks;

namespace EcfDgii.Client.Infrastructure.Dgii
{
    /// <summary>Extracted so DgiiDirectTransport can be unit-tested with a test double (EcfTokenManager
    /// has no virtual members, so Moq can't proxy it directly) and so InvalidateAsync — the reactive-401
    /// hook — is part of the contract transport code depends on, not an implementation detail.</summary>
    public interface IEcfTokenManager
    {
        Task<string> GetTokenAsync(CancellationToken ct = default);

        /// <summary>Discards the cached token so the next GetTokenAsync call is forced to renew.</summary>
        Task InvalidateAsync(CancellationToken ct = default);
    }
}
