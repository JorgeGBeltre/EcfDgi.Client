using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using EcfDgii.Client.Domain.Interfaces;

namespace EcfDgii.Client.Infrastructure.Persistence
{
    public class MemorySequenceProvider : IEcfSequenceProvider
    {
        private readonly Dictionary<string, long> _sequences = new Dictionary<string, long>();

        public Task<string> GetNextAsync(string rncEmisor, CancellationToken ct = default)
        {
            if (!_sequences.ContainsKey(rncEmisor)) _sequences[rncEmisor] = 1;
            else _sequences[rncEmisor]++;

            return Task.FromResult($"E31{_sequences[rncEmisor]:D10}");
        }

        public Task ReleaseAsync(string rncEmisor, string eNcf, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }
}
