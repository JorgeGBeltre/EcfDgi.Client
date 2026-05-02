using System;
using System.Threading;
using System.Threading.Tasks;

namespace EcfDgii.Client.Domain.Interfaces
{
    public interface IEcfSequenceProvider
    {
        Task<string> GetNextAsync(string rncEmisor, CancellationToken ct = default);
        Task ReleaseAsync(string rncEmisor, string eNcf, CancellationToken ct = default);
    }
}
